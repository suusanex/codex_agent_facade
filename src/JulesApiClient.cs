using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Jules REST の失敗。応答が無く受理状況が不明なときは <see cref="JulesTransportException"/>。
/// 4xx のように API が拒否したときは <see cref="JulesRejectedException"/>。
/// </summary>
public class JulesApiException : Exception
{
    public JulesApiException(string message)
        : base(message)
    {
    }

    public JulesApiException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed class JulesTransportException : JulesApiException
{
    public JulesTransportException(string message)
        : base(message)
    {
    }

    public JulesTransportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed class JulesRejectedException : JulesApiException
{
    public JulesRejectedException(string message)
        : base(message)
    {
    }

    public JulesRejectedException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class JulesCredentialException : JulesApiException
{
    public JulesCredentialException(string message)
        : base(message)
    {
    }
}

public sealed record JulesSource(string Name, string Owner, string Repository);

public sealed record JulesSession(
    string SessionId,
    string? Url,
    string? State,
    string? SourceName,
    string? StartingBranch);

public sealed record JulesPatch(string? BaseCommitId, string? Diff);

public sealed record JulesActivity(
    string Id,
    DateTimeOffset? CreatedAt,
    string? UserMessage,
    string? AgentMessage,
    bool SessionCompleted,
    bool SessionFailed,
    string? FailureReason,
    IReadOnlyList<JulesPatch> Patches);

/// <summary>
/// Jules REST client。通信は BCL の HttpClient と System.Text.Json だけを使う。
/// TypeScript の jules-sdk は別 runtime になるため採用しない。
/// </summary>
public sealed class JulesApiClient
{
    public const string ApiKeyEnvironmentVariable = "JULES_API_KEY";
    public static readonly Uri BaseAddress = new("https://jules.googleapis.com/v1alpha/");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;

    public JulesApiClient(HttpMessageHandler handler, Func<string?>? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromMinutes(2),
        };
        _apiKey = apiKey ?? (() => Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable));
    }

    public async Task<IReadOnlyList<JulesSource>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = new List<JulesSource>();
        string? pageToken = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var relative = "sources?pageSize=100" + Page(pageToken);
            var body = await SendAsync(HttpMethod.Get, relative, null, cancellationToken).ConfigureAwait(false);
            var page = Deserialize<SourceList>(body, "sources");
            if (page.Sources is not null)
            {
                foreach (var source in page.Sources)
                {
                    sources.Add(ToSource(source));
                }
            }

            pageToken = Next(page.NextPageToken, seen);
        }
        while (pageToken is not null);

        return sources;
    }

    public async Task<JulesSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var body = await SendAsync(HttpMethod.Get, "sessions/" + Escape(sessionId), null, cancellationToken).ConfigureAwait(false);
        return ToSession(Deserialize<SessionDto>(body, "session"), required: true);
    }

    public async Task<JulesSession> CreateSessionAsync(
        string prompt,
        string source,
        string startingBranch,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new CreateSessionRequest(
            prompt,
            new SourceContextDto(source, new GitHubRepoContextDto(startingBranch)),
            RequirePlanApproval: false), Json);
        var body = await SendAsync(HttpMethod.Post, "sessions", payload, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new JulesTransportException("Jules session create returned an empty body.");
        }

        // 2xx でも JSON を読めない、または session ID を確定できない応答は拒否ではない。
        // session が作成済みの可能性があるため、呼び出し側は submission_unknown として再 POST しない。
        try
        {
            return ToSession(Deserialize<SessionDto>(body, "session"), required: true);
        }
        catch (JulesRejectedException ex)
        {
            CliJson.TraceException(ex);
            throw new JulesTransportException("Jules session create response could not be read.", ex);
        }
    }

    public Task SendMessageAsync(string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new SendMessageRequest(prompt), Json);
        return SendAsync(HttpMethod.Post, "sessions/" + Escape(sessionId) + ":sendMessage", payload, cancellationToken);
    }

    public async Task<IReadOnlyList<JulesActivity>> ListActivitiesAsync(string sessionId, CancellationToken cancellationToken)
    {
        var activities = new List<JulesActivity>();
        string? pageToken = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var relative = "sessions/" + Escape(sessionId) + "/activities?pageSize=100" + Page(pageToken);
            var body = await SendAsync(HttpMethod.Get, relative, null, cancellationToken).ConfigureAwait(false);
            var page = Deserialize<ActivityList>(body, "activities");
            if (page.Activities is not null)
            {
                foreach (var activity in page.Activities)
                {
                    activities.Add(ToActivity(activity));
                }
            }

            pageToken = Next(page.NextPageToken, seen);
        }
        while (pageToken is not null);

        return activities;
    }

    private async Task<string> SendAsync(HttpMethod method, string relative, string? jsonBody, CancellationToken cancellationToken)
    {
        var apiKey = _apiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new JulesCredentialException("JULES_API_KEY is not set.");
        }

        using var request = new HttpRequestMessage(method, relative);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw new JulesTransportException("Jules API request failed.", ex);
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw new JulesTransportException("Jules API response could not be read.", ex);
        }

        var status = (int)response.StatusCode;
        if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429)
        {
            throw new JulesTransportException("Jules API returned status " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new JulesRejectedException(
                "Jules API rejected the request with status " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                response.StatusCode);
        }

        return body;
    }

    private static T Deserialize<T>(string body, string label)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, Json);
            if (value is null)
            {
                throw new JulesRejectedException("Jules " + label + " response was empty.");
            }

            return value;
        }
        catch (JulesApiException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            CliJson.TraceException(ex);
            throw new JulesRejectedException("Jules " + label + " response was not valid JSON.");
        }
    }

    private static JulesSource ToSource(SourceDto source)
    {
        var name = source.Name ?? string.Empty;
        var owner = source.GithubRepo?.Owner ?? string.Empty;
        var repo = source.GithubRepo?.Repo ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            throw new JulesRejectedException("Jules source did not include a GitHub owner and repository.");
        }

        return new JulesSource(name, owner, repo);
    }

    private static JulesSession ToSession(SessionDto session, bool required)
    {
        var sessionId = SessionIdFromName(session.Name) ?? session.Id;
        if (required && string.IsNullOrWhiteSpace(sessionId))
        {
            throw new JulesTransportException("Jules session response did not include a session resource name.");
        }

        return new JulesSession(
            sessionId ?? string.Empty,
            session.Url,
            session.State,
            session.SourceContext?.Source,
            session.SourceContext?.GithubRepoContext?.StartingBranch);
    }

    /// <summary>
    /// REST の path は sessions/{session} である。公開例では name の末尾と id フィールドが違うため、path に使う name の末尾を session id にする。
    /// </summary>
    internal static string? SessionIdFromName(string? name)
    {
        const string prefix = "sessions/";
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var suffix = name[prefix.Length..];
        return suffix.Length == 0 || suffix.Contains('/', StringComparison.Ordinal) ? null : suffix;
    }

    private static JulesActivity ToActivity(ActivityDto activity)
    {
        var id = activity.Id;
        if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(activity.Name))
        {
            var slash = activity.Name.LastIndexOf('/');
            id = slash >= 0 ? activity.Name[(slash + 1)..] : activity.Name;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new JulesRejectedException("Jules activity did not include an id.");
        }

        var patches = new List<JulesPatch>();
        if (activity.Artifacts is not null)
        {
            foreach (var artifact in activity.Artifacts)
            {
                var patch = artifact.ChangeSet?.GitPatch;
                if (patch is null)
                {
                    continue;
                }

                patches.Add(new JulesPatch(patch.BaseCommitId, patch.UnidiffPatch));
            }
        }

        return new JulesActivity(
            id,
            activity.CreateTime,
            activity.UserMessaged?.UserMessage,
            activity.AgentMessaged?.AgentMessage,
            activity.SessionCompleted is not null,
            activity.SessionFailed is not null,
            activity.SessionFailed?.Reason,
            patches);
    }

    private static string? Next(string? pageToken, HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(pageToken))
        {
            return null;
        }

        if (!seen.Add(pageToken))
        {
            throw new JulesRejectedException("Jules pagination repeated a page token.");
        }

        return pageToken;
    }

    private static string Page(string? pageToken)
    {
        return string.IsNullOrEmpty(pageToken) ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken);
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private sealed record CreateSessionRequest(
        string Prompt,
        SourceContextDto SourceContext,
        bool RequirePlanApproval);

    private sealed record SendMessageRequest(string Prompt);

    private sealed record SourceContextDto(
        string Source,
        GitHubRepoContextDto GithubRepoContext);

    private sealed record GitHubRepoContextDto(string StartingBranch);

    private sealed class SourceList
    {
        public List<SourceDto>? Sources { get; set; }

        public string? NextPageToken { get; set; }
    }

    private sealed class ActivityList
    {
        public List<ActivityDto>? Activities { get; set; }

        public string? NextPageToken { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Name { get; set; }

        public GitHubRepoDto? GithubRepo { get; set; }
    }

    private sealed class GitHubRepoDto
    {
        public string? Owner { get; set; }

        public string? Repo { get; set; }
    }

    private sealed class SessionDto
    {
        public string? Name { get; set; }

        public string? Id { get; set; }

        public string? State { get; set; }

        public string? Url { get; set; }

        public SourceContextResponse? SourceContext { get; set; }
    }

    private sealed class SourceContextResponse
    {
        public string? Source { get; set; }

        public GitHubRepoContextResponse? GithubRepoContext { get; set; }
    }

    private sealed class GitHubRepoContextResponse
    {
        public string? StartingBranch { get; set; }
    }

    private sealed class ActivityDto
    {
        public string? Name { get; set; }

        public string? Id { get; set; }

        public DateTimeOffset? CreateTime { get; set; }

        public List<ArtifactDto>? Artifacts { get; set; }

        public UserMessagedDto? UserMessaged { get; set; }

        public AgentMessagedDto? AgentMessaged { get; set; }

        public SessionCompletedDto? SessionCompleted { get; set; }

        public SessionFailedDto? SessionFailed { get; set; }
    }

    private sealed class UserMessagedDto
    {
        public string? UserMessage { get; set; }
    }

    private sealed class AgentMessagedDto
    {
        public string? AgentMessage { get; set; }
    }

    private sealed class SessionCompletedDto
    {
    }

    private sealed class SessionFailedDto
    {
        public string? Reason { get; set; }
    }

    private sealed class ArtifactDto
    {
        public ChangeSetDto? ChangeSet { get; set; }
    }

    private sealed class ChangeSetDto
    {
        public GitPatchDto? GitPatch { get; set; }
    }

    private sealed class GitPatchDto
    {
        public string? BaseCommitId { get; set; }

        public string? UnidiffPatch { get; set; }
    }
}

public interface IGitWorktreeInspector
{
    GitWorktreeInspection Inspect(string workingDirectory);

    string? ReadRemoteTip(string remoteUrl, string branch);

    IReadOnlyList<GitRemoteBranchTip> ListBranchTips(string remoteUrl);
}

public sealed record GitWorktreeInspection(
    bool IsRepository,
    bool IsDirty,
    bool IsDetached,
    string? HeadCommit,
    string? BranchName,
    string? UpstreamRemote,
    string? UpstreamBranch,
    IReadOnlyList<GitRemoteEndpoint> Remotes);

public sealed record GitRemoteEndpoint(string Name, string Url, string? Owner, string? Repository);

public sealed record GitRemoteBranchTip(string Branch, string Commit);

/// <summary>
/// git の参照専用コマンド。commit / push / fetch / checkout は実行しない。
/// </summary>
public sealed class GitWorktreeInspector : IGitWorktreeInspector
{
    public GitWorktreeInspection Inspect(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var inside = Run(workingDirectory, "rev-parse", "--is-inside-work-tree");
        if (inside.ExitCode != 0 || !string.Equals(inside.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new GitWorktreeInspection(false, false, false, null, null, null, null, []);
        }

        var status = Require(workingDirectory, "status", "--porcelain", "--untracked-files=normal").Trim();
        var head = Require(workingDirectory, "rev-parse", "HEAD").Trim();
        var branch = Require(workingDirectory, "branch", "--show-current").Trim();
        var detached = branch.Length == 0;
        string? upstreamRemote = null;
        string? upstreamBranch = null;
        if (!detached)
        {
            var remote = Run(workingDirectory, "config", "--get", "branch." + branch + ".remote");
            var merge = Run(workingDirectory, "config", "--get", "branch." + branch + ".merge");
            if (remote.ExitCode == 0 && merge.ExitCode == 0)
            {
                upstreamRemote = remote.Stdout.Trim();
                var mergeRef = merge.Stdout.Trim();
                const string prefix = "refs/heads/";
                upstreamBranch = mergeRef.StartsWith(prefix, StringComparison.Ordinal) ? mergeRef[prefix.Length..] : mergeRef;
            }
        }

        var remotes = new List<GitRemoteEndpoint>();
        var names = Require(workingDirectory, "remote");
        foreach (var name in names.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var url = Require(workingDirectory, "remote", "get-url", name).Trim();
            TryParseGitHubRepository(url, out var owner, out var repo);
            remotes.Add(new GitRemoteEndpoint(name, url, owner, repo));
        }

        return new GitWorktreeInspection(
            true,
            status.Length > 0,
            detached,
            head,
            detached ? null : branch,
            string.IsNullOrWhiteSpace(upstreamRemote) ? null : upstreamRemote,
            string.IsNullOrWhiteSpace(upstreamBranch) ? null : upstreamBranch,
            remotes);
    }

    public string? ReadRemoteTip(string remoteUrl, string branch)
    {
        var output = Require(null, "ls-remote", remoteUrl, "refs/heads/" + branch);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t');
            var sha = (tab < 0 ? line : line[..tab]).Trim();
            return sha.Length == 0 ? null : sha;
        }

        return null;
    }

    public IReadOnlyList<GitRemoteBranchTip> ListBranchTips(string remoteUrl)
    {
        var output = Require(null, "ls-remote", "--heads", remoteUrl);
        var tips = new List<GitRemoteBranchTip>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            const string prefix = "refs/heads/";
            var reference = line[(tab + 1)..].Trim();
            if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            tips.Add(new GitRemoteBranchTip(reference[prefix.Length..], line[..tab].Trim()));
        }

        return tips;
    }

    internal static bool TryParseGitHubRepository(string url, out string? owner, out string? repository)
    {
        owner = null;
        repository = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var text = url.Trim();
        string path;
        if (text.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = text["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            path = uri.AbsolutePath.TrimStart('/');
        }
        else
        {
            return false;
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        owner = parts[0];
        repository = parts[1];
        return true;
    }

    private static string Require(string? workingDirectory, params string[] args)
    {
        var output = Run(workingDirectory, args);
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException("git " + string.Join(' ', args) + " failed with exit code " + output.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        return output.Stdout;
    }

    private readonly record struct GitCommandOutput(int ExitCode, string Stdout, string Stderr);

    private static GitCommandOutput Run(string? workingDirectory, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                start.WorkingDirectory = workingDirectory;
            }

            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = start };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start git.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new GitCommandOutput(process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw new InvalidOperationException("Failed to run git.", ex);
        }
    }
}

/// <summary>
/// 同じ job store を使う process 間で、Jules session への送信をファイル排他する。
/// 排他の本体は、同じ session の未 settled job と job record の SessionLockHeld である。
/// このファイルは送信区間の相互排除に使う。解放は file を先に空にし、その後 record の lock を下ろす。
/// </summary>
internal sealed class JulesSessionGate : IAsyncDisposable
{
    private readonly FileStream _stream;

    private JulesSessionGate(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<JulesSessionGate> EnterAsync(
        string jobStoreDirectory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(jobStoreDirectory, "jules-locks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Hash(sessionId) + ".lock");
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new JulesSessionGate(stream);
            }
            catch (IOException ex) when (attempt < 10)
            {
                CliJson.TraceException(ex);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Jules session lock is busy.");
    }

    public static async Task ReleaseAsync(
        string jobStoreDirectory,
        string sessionId,
        string jobId,
        CancellationToken cancellationToken)
    {
        await using var gate = await EnterAsync(jobStoreDirectory, sessionId, cancellationToken).ConfigureAwait(false);
        if (string.Equals(gate.ReadOwnerJobId(), jobId, StringComparison.Ordinal))
        {
            gate._stream.SetLength(0);
            gate._stream.Flush(flushToDisk: true);
        }
    }

    public string? ReadOwnerJobId()
    {
        _stream.Position = 0;
        using var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var text = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LockFile>(text, AgentJson.Options)?.JobId;
        }
        catch (JsonException ex)
        {
            CliJson.TraceException(ex);
            throw new InvalidOperationException("Jules session lock file is invalid.", ex);
        }
    }

    public void WriteOwner(string sessionId, string jobId, string requestId)
    {
        var json = JsonSerializer.Serialize(new LockFile(sessionId, jobId, requestId), AgentJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        _stream.Position = 0;
        _stream.SetLength(0);
        _stream.Write(bytes);
        _stream.Flush(flushToDisk: true);
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record LockFile(string SessionId, string JobId, string RequestId);
}
