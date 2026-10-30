using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Jules の 1 job。再開時は保存済み checkpoint だけを使い、POST を再送しない。
/// </summary>
public sealed class JulesRunContext
{
    public required string JobId { get; init; }

    public required string RequestId { get; init; }

    public required string JobStoreDirectory { get; init; }

    public required Action<JulesJobState> Persist { get; init; }

    public required Func<string, bool> IsSessionLockedByOther { get; init; }

    public JulesJobState? Resume { get; init; }

    public string? EventsLogPath { get; init; }

    public string? TextLogPath { get; init; }
}

internal abstract record JulesRunOutcome;

internal sealed record JulesCompletedOutcome(AgentRunResult Result) : JulesRunOutcome;

internal sealed record JulesWaitingOutcome(AgentRunResult Result) : JulesRunOutcome;

internal sealed record JulesFailedOutcome(Exception Exception, bool ReleaseSessionLock, string? SessionId) : JulesRunOutcome;

internal sealed record JulesSuspendedOutcome : JulesRunOutcome;

/// <summary>
/// Jules の REST adapter。MCP job host からだけ実行し、単発 CLI では起動前に拒否する。
/// </summary>
public sealed class JulesDriver
{
    private readonly JulesApiClient _api;
    private readonly IGitWorktreeInspector _git;
    private readonly Func<CancellationToken, Task> _pollDelay;

    public JulesDriver()
        : this(new SocketsHttpHandler(), new GitWorktreeInspector(), null, null)
    {
    }

    internal JulesDriver(
        HttpMessageHandler handler,
        IGitWorktreeInspector git,
        Func<CancellationToken, Task>? pollDelay,
        Func<string?>? apiKey)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(git);
        _api = new JulesApiClient(handler, apiKey);
        _git = git;
        _pollDelay = pollDelay ?? (cancellationToken => Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
    }

    internal async Task<JulesRunOutcome> RunAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        JulesRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runLog);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            if (context.Resume?.SessionId is not null)
            {
                return await MonitorAsync(NormalizeResumed(context.Resume), context, runLog, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                return await ContinueAsync(request, runLog, context, cancellationToken).ConfigureAwait(false);
            }

            return await CreateAsync(request, runLog, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JulesCredentialException ex)
        {
            CliJson.TraceException(ex);
            if (context.Resume?.SessionId is not null)
            {
                context.Persist(context.Resume with { RemotePhase = JulesRemotePhase.CommunicationError });
                return new JulesSuspendedOutcome();
            }

            return Fail(ex, "credential_missing", releaseLock: false, sessionId: null);
        }
        catch (JulesRunRejectedException ex)
        {
            CliJson.TraceException(ex);
            return Fail(ex, ex.Kind, ex.ReleaseSessionLock, ex.SessionId);
        }
    }

    private async Task<JulesRunOutcome> CreateAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        JulesRunContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JulesSource> sources;
        (string SourceName, string Branch, string Owner, string Repository) target;
        try
        {
            sources = await _api.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
            target = ResolveNewSession(request.WorkingDirectory, sources);
        }
        catch (JulesRunRejectedException)
        {
            throw;
        }
        catch (JulesCredentialException)
        {
            throw;
        }
        catch (JulesTransportException ex)
        {
            CliJson.TraceException(ex);
            return Fail(ex, "communication_error", releaseLock: false, sessionId: null);
        }
        catch (JulesRejectedException ex)
        {
            CliJson.TraceException(ex);
            return Fail(ex, "remote_rejected", releaseLock: false, sessionId: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CliJson.TraceException(ex);
            return Fail(ex, "repository_rejected", releaseLock: false, sessionId: null);
        }
        var state = NewState(request.Prompt, continuation: false, context, runLog) with
        {
            RemotePhase = JulesRemotePhase.SubmittingCreate,
            SourceName = target.SourceName,
            RepositoryOwner = target.Owner,
            RepositoryName = target.Repository,
            StartingBranch = target.Branch,
        };
        context.Persist(state);
        runLog.WriteStarted(Started(request, "jules-api", ["POST", "sessions"]));
        try
        {
            var created = await _api.CreateSessionAsync(request.Prompt, target.SourceName, target.Branch, cancellationToken).ConfigureAwait(false);
            state = state with
            {
                SessionId = created.SessionId,
                SessionUrl = created.Url,
                RemotePhase = JulesRemotePhase.Polling,
            };
            context.Persist(state);
        }
        catch (JulesTransportException ex)
        {
            CliJson.TraceException(ex);
            return Fail(
                new InvalidOperationException("Jules session create result is unknown. The request was not sent again."),
                "submission_unknown",
                releaseLock: false,
                sessionId: null);
        }
        catch (JulesRejectedException ex)
        {
            CliJson.TraceException(ex);
            return Fail(ex, "remote_rejected", releaseLock: false, sessionId: null);
        }

        return await MonitorAsync(state, context, runLog, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JulesRunOutcome> ContinueAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        JulesRunContext context,
        CancellationToken cancellationToken)
    {
        var sessionId = request.SessionId!.Trim();
        JulesSessionGate gate;
        try
        {
            gate = await JulesSessionGate.EnterAsync(context.JobStoreDirectory, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            CliJson.TraceException(ex);
            return Fail(ex, "session_locked", releaseLock: false, sessionId);
        }

        JulesJobState state;
        await using (gate)
        {
            // lock file の owner 文字列だけでは拒否しない。解放の途中で落ちると、誰も持っていない古い owner が残る。
            // 二重送信の本体は、同じ session の非 settled job と SessionLockHeld である。ここを通過したあとの WriteOwner が古いファイルを上書きする。
            if (context.IsSessionLockedByOther(sessionId))
            {
                throw Reject(
                    "session_locked",
                    "Jules session is locked by another job until its message submission is resolved.",
                    releaseLock: false,
                    sessionId);
            }

            JulesSession session;
            IReadOnlyList<JulesActivity> existing;
            try
            {
                session = await _api.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                existing = await _api.ListActivitiesAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (JulesTransportException ex)
            {
                CliJson.TraceException(ex);
                return Fail(ex, "communication_error", releaseLock: false, sessionId);
            }
            catch (JulesRejectedException ex)
            {
                CliJson.TraceException(ex);
                return Fail(ex, "remote_rejected", releaseLock: false, sessionId);
            }

            if (!string.Equals(session.State, "AWAITING_USER_FEEDBACK", StringComparison.Ordinal))
            {
                throw Reject(
                    "invalid_request",
                    "Jules continuation only accepts a session in AWAITING_USER_FEEDBACK. The session state is " + (session.State ?? "unknown") + ".",
                    releaseLock: false,
                    sessionId);
            }

            var source = await RequireSourceAsync(session, cancellationToken).ConfigureAwait(false);
            EnsureContinuationRepository(request.WorkingDirectory, source);
            state = NewState(request.Prompt, continuation: true, context, runLog) with
            {
                RemotePhase = JulesRemotePhase.SubmittingMessage,
                SessionId = sessionId,
                SessionUrl = session.Url,
                ActivityBaseline = existing.Select(activity => activity.Id).ToArray(),
                SessionLockHeld = true,
                SourceName = source.Name,
                RepositoryOwner = source.Owner,
                RepositoryName = source.Repository,
                StartingBranch = session.StartingBranch,
            };
            context.Persist(state);
            gate.WriteOwner(sessionId, context.JobId, context.RequestId);
            runLog.WriteStarted(Started(request, "jules-api", ["POST", "sessions/" + sessionId + ":sendMessage"]));
            try
            {
                await _api.SendMessageAsync(sessionId, request.Prompt, cancellationToken).ConfigureAwait(false);
                state = state with { RemotePhase = JulesRemotePhase.Polling };
                context.Persist(state);
            }
            catch (JulesTransportException ex)
            {
                CliJson.TraceException(ex);
                state = state with { RemotePhase = JulesRemotePhase.SubmissionUnknown };
                context.Persist(state);
            }
            catch (JulesRejectedException ex)
            {
                CliJson.TraceException(ex);
                return Fail(ex, "remote_rejected", releaseLock: true, sessionId);
            }
        }

        return await MonitorAsync(state, context, runLog, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JulesRunOutcome> MonitorAsync(
        JulesJobState state,
        JulesRunContext context,
        IAgentRunLog runLog,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.SessionId))
        {
            return Fail(
                new InvalidOperationException("Jules session create result is unknown. The request was not sent again."),
                "submission_unknown",
                releaseLock: false,
                sessionId: null);
        }

        string? stableCompletedSignature = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JulesSession session;
            try
            {
                session = await _api.GetSessionAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (HandleMonitorReadFailure(ex, state, context, out var outcome))
            {
                return outcome!;
            }

            IReadOnlyList<JulesActivity> activities;
            try
            {
                activities = await _api.ListActivitiesAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (JulesRejectedException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // 作成直後は session GET が成功しても activities が 404 になる。同じ URL は直後に 200 を返す。
                CliJson.TraceException(ex);
                await _pollDelay(cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (HandleMonitorReadFailure(ex, state, context, out var outcome))
            {
                return outcome!;
            }

            var baseline = new HashSet<string>(state.ActivityBaseline, StringComparer.Ordinal);
            var fresh = activities.Where(activity => !baseline.Contains(activity.Id)).ToList();
            var completed = string.Equals(session.State, "COMPLETED", StringComparison.Ordinal);
            var signature = ActivitySignature(activities);
            if (state.Continuation && !TryCorrelate(state, fresh, out var correlationFailure))
            {
                if (correlationFailure is not null)
                {
                    return correlationFailure;
                }

                if (completed && string.Equals(stableCompletedSignature, signature, StringComparison.Ordinal))
                {
                    // 該当する userMessaged がまだ無い。session が先に COMPLETED でも受理は未解決なので terminal にしない。
                    // lock を残し、次の get / wait が同じ job を再照会する。
                    state = state with
                    {
                        RemotePhase = JulesRemotePhase.SubmissionUnknown,
                        SessionLockHeld = true,
                    };
                    context.Persist(state);
                    return new JulesSuspendedOutcome();
                }

                if (state.RemotePhase == JulesRemotePhase.SubmittingMessage)
                {
                    state = state with { RemotePhase = JulesRemotePhase.SubmissionUnknown };
                    context.Persist(state);
                }

                stableCompletedSignature = completed ? signature : null;
                await _pollDelay(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var hasCompletedActivity = fresh.Any(activity => activity.SessionCompleted);
            var acceptStableCompleted = completed
                && !hasCompletedActivity
                && string.Equals(stableCompletedSignature, signature, StringComparison.Ordinal);
            if (completed && !hasCompletedActivity && !acceptStableCompleted)
            {
                // 実測の session 3295034226344094016 は state=COMPLETED になったが sessionCompleted activity は無かった。
                // 同じ activity 集合が次の poll でも変わらないときだけ、今回分の agent message で完了する。
                stableCompletedSignature = signature;
                context.Persist(state with { RemotePhase = state.RemotePhase });
                await _pollDelay(cancellationToken).ConfigureAwait(false);
                continue;
            }

            stableCompletedSignature = null;

            if (state.RemotePhase is JulesRemotePhase.SubmissionUnknown or JulesRemotePhase.SubmittingMessage)
            {
                state = state with { RemotePhase = JulesRemotePhase.Polling };
                context.Persist(state);
            }

            IReadOnlyList<AgentJobArtifact> artifacts;
            try
            {
                artifacts = SavePatches(context.JobStoreDirectory, context.JobId, fresh, state.Artifacts ?? []);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CliJson.TraceException(ex);
                context.Persist(state);
                return Fail(ex, "artifact_persist_failed", releaseLock: true, state.SessionId);
            }

            var messages = fresh
                .Select(activity => activity.AgentMessage)
                .Where(message => message is not null)
                .Cast<string>()
                .ToArray();
            var output = string.Join("\n\n", messages);
            state = state with
            {
                Artifacts = artifacts,
                SessionUrl = session.Url ?? state.SessionUrl,
            };
            runLog.AppendHumanFragment("jules", output);

            if (completed && (hasCompletedActivity || acceptStableCompleted))
            {
                return new JulesCompletedOutcome(BuildResult(context, state, output, JulesRemotePhase.Completed, inputPrompt: null, runLog));
            }

            if (string.Equals(session.State, "FAILED", StringComparison.Ordinal)
                && fresh.Any(activity => activity.SessionFailed))
            {
                var reason = fresh.Select(activity => activity.FailureReason).FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
                return Fail(
                    new InvalidOperationException("Jules session failed. " + (reason ?? "The session failed activity did not include a reason.")),
                    "remote_failed",
                    releaseLock: true,
                    state.SessionId);
            }

            if (string.Equals(session.State, "AWAITING_PLAN_APPROVAL", StringComparison.Ordinal))
            {
                return Fail(
                    new InvalidOperationException("Jules is waiting for plan approval. This Facade does not approve plans."),
                    "agent_error",
                    releaseLock: true,
                    state.SessionId);
            }

            if (string.Equals(session.State, "AWAITING_USER_FEEDBACK", StringComparison.Ordinal) && messages.Length > 0)
            {
                var waiting = state with
                {
                    InputPrompt = output,
                    RemotePhase = JulesRemotePhase.AwaitingUserFeedback,
                };
                context.Persist(waiting);
                return new JulesWaitingOutcome(BuildResult(context, waiting, output, JulesRemotePhase.AwaitingUserFeedback, output, runLog));
            }

            context.Persist(state with { RemotePhase = state.RemotePhase });
            await _pollDelay(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HandleMonitorReadFailure(
        Exception exception,
        JulesJobState state,
        JulesRunContext context,
        out JulesRunOutcome? outcome)
    {
        outcome = null;
        if (exception is JulesCredentialException or OperationCanceledException)
        {
            return false;
        }

        if (exception is JulesTransportException)
        {
            CliJson.TraceException(exception);
            outcome = SuspendMonitor(state, context);
            return true;
        }

        if (exception is JulesRejectedException rejected)
        {
            CliJson.TraceException(exception);
            // session GET の 404 だけは session 消失として terminal にする。401/403 や 2xx の parse 不能は監視側の失敗である。
            if (rejected.StatusCode == HttpStatusCode.NotFound)
            {
                outcome = Fail(exception, "remote_rejected", releaseLock: false, state.SessionId);
                return true;
            }

            outcome = SuspendMonitor(state, context);
            return true;
        }

        return false;
    }

    private static JulesSuspendedOutcome SuspendMonitor(JulesJobState state, JulesRunContext context)
    {
        var phase = state.RemotePhase is JulesRemotePhase.SubmissionUnknown or JulesRemotePhase.SubmittingMessage
            ? JulesRemotePhase.SubmissionUnknown
            : JulesRemotePhase.CommunicationError;
        context.Persist(state with { RemotePhase = phase });
        return new JulesSuspendedOutcome();
    }

    private static string ActivitySignature(IReadOnlyList<JulesActivity> activities)
    {
        return string.Join('\n', activities.Select(activity => activity.Id).Order(StringComparer.Ordinal));
    }

    private static bool TryCorrelate(JulesJobState state, List<JulesActivity> fresh, out JulesFailedOutcome? failure)
    {
        failure = null;
        var matches = fresh
            .Where(activity => activity.UserMessage is not null && string.Equals(HashPrompt(activity.UserMessage), state.PromptSha256, StringComparison.Ordinal))
            .ToList();
        if (matches.Count > 1)
        {
            failure = Fail(
                new InvalidOperationException("Jules continuation found multiple new userMessaged activities for this prompt. This job was not completed from those activities."),
                "activity_correlation_failed",
                releaseLock: false,
                state.SessionId);
            return false;
        }

        return matches.Count == 1;
    }

    private static JulesJobState NormalizeResumed(JulesJobState state)
    {
        if (state.RemotePhase is JulesRemotePhase.SubmittingMessage)
        {
            return state with { RemotePhase = JulesRemotePhase.SubmissionUnknown };
        }

        return state;
    }

    private async Task<JulesSource> RequireSourceAsync(JulesSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SourceName))
        {
            throw Reject("repository_rejected", "Jules session did not include a source.", false, session.SessionId);
        }

        var sources = await _api.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        var matches = sources.Where(source => string.Equals(source.Name, session.SourceName, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            throw Reject("repository_rejected", "Jules session source was not found in the connected sources.", false, session.SessionId);
        }

        return matches[0];
    }

    private (string SourceName, string Branch, string Owner, string Repository) ResolveNewSession(
        string workingDirectory,
        IReadOnlyList<JulesSource> sources)
    {
        var inspection = _git.Inspect(workingDirectory);
        if (!inspection.IsRepository)
        {
            throw Reject("repository_rejected", "working_directory is not a git repository.", false, null);
        }

        if (inspection.IsDirty)
        {
            throw Reject("repository_rejected", "Jules requires a clean worktree when creating a session.", false, null);
        }

        if (string.IsNullOrWhiteSpace(inspection.HeadCommit))
        {
            throw Reject("repository_rejected", "Jules could not read HEAD.", false, null);
        }

        if (!inspection.IsDetached)
        {
            if (string.IsNullOrWhiteSpace(inspection.UpstreamRemote) || string.IsNullOrWhiteSpace(inspection.UpstreamBranch))
            {
                throw Reject("repository_rejected", "Jules requires the configured upstream of the attached branch.", false, null);
            }

            var remote = inspection.Remotes.SingleOrDefault(candidate => string.Equals(candidate.Name, inspection.UpstreamRemote, StringComparison.Ordinal));
            if (remote is null || string.IsNullOrWhiteSpace(remote.Owner) || string.IsNullOrWhiteSpace(remote.Repository))
            {
                throw Reject("repository_rejected", "The upstream remote is not a GitHub repository.", false, null);
            }

            var source = MatchSource(sources, remote.Owner, remote.Repository);
            var tip = _git.ReadRemoteTip(remote.Url, inspection.UpstreamBranch);
            if (!SameCommit(tip, inspection.HeadCommit))
            {
                throw Reject("repository_rejected", "The upstream branch tip does not match HEAD.", false, null);
            }

            return (source.Name, inspection.UpstreamBranch, remote.Owner, remote.Repository);
        }

        var candidates = new List<(string SourceName, string Branch, string Owner, string Repository)>();
        foreach (var remote in inspection.Remotes)
        {
            if (string.IsNullOrWhiteSpace(remote.Owner) || string.IsNullOrWhiteSpace(remote.Repository))
            {
                continue;
            }

            JulesSource source;
            try
            {
                source = MatchSource(sources, remote.Owner, remote.Repository);
            }
            catch (JulesRunRejectedException)
            {
                continue;
            }

            foreach (var tip in _git.ListBranchTips(remote.Url))
            {
                if (SameCommit(tip.Commit, inspection.HeadCommit))
                {
                    candidates.Add((source.Name, tip.Branch, source.Owner, source.Repository));
                }
            }
        }

        var unique = candidates.Distinct().ToList();
        if (unique.Count != 1)
        {
            throw Reject(
                "repository_rejected",
                unique.Count == 0
                    ? "No unique GitHub branch tip matches HEAD."
                    : "More than one GitHub branch tip matches HEAD.",
                false,
                null);
        }

        return unique[0];
    }

    private void EnsureContinuationRepository(string workingDirectory, JulesSource source)
    {
        var inspection = _git.Inspect(workingDirectory);
        if (!inspection.IsRepository)
        {
            throw Reject("repository_rejected", "working_directory is not a git repository.", false, null);
        }

        var matched = inspection.Remotes.Any(remote =>
            string.Equals(remote.Owner, source.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(remote.Repository, source.Repository, StringComparison.OrdinalIgnoreCase));
        if (!matched)
        {
            throw Reject("repository_rejected", "working_directory does not match the Jules session repository.", false, null);
        }
    }

    private static JulesSource MatchSource(IReadOnlyList<JulesSource> sources, string owner, string repository)
    {
        var matches = sources
            .Where(source => string.Equals(source.Owner, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.Repository, repository, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            throw Reject(
                "repository_rejected",
                matches.Count == 0
                    ? "No connected Jules source matches the GitHub repository."
                    : "More than one Jules source matches the GitHub repository.",
                false,
                null);
        }

        return matches[0];
    }

    private static List<AgentJobArtifact> SavePatches(
        string jobStoreDirectory,
        string jobId,
        IReadOnlyList<JulesActivity> activities,
        IReadOnlyList<AgentJobArtifact> existing)
    {
        var saved = existing.ToList();
        foreach (var activity in activities)
        {
            var index = 0;
            foreach (var patch in activity.Patches)
            {
                var suffix = index.ToString(CultureInfo.InvariantCulture);
                index++;
                if (string.IsNullOrWhiteSpace(patch.Diff))
                {
                    // 実測では progress 中の gitPatch が baseCommitId だけで届く。diff が無いものは保存しない。
                    continue;
                }

                if (string.IsNullOrWhiteSpace(patch.BaseCommitId))
                {
                    throw new InvalidOperationException("Jules git patch did not include a base commit and a diff.");
                }

                if (activity.CreatedAt is null)
                {
                    throw new InvalidOperationException("Jules git patch activity did not include createTime.");
                }

                var path = Path.Combine(jobStoreDirectory, "jules-artifacts", jobId, activity.Id + "-" + suffix + ".patch");
                if (saved.Any(artifact => string.Equals(artifact.PatchPath, path, StringComparison.Ordinal)))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new InvalidOperationException("Jules artifact path is invalid.");
                }

                Directory.CreateDirectory(directory);
                var bytes = Encoding.UTF8.GetBytes(patch.Diff);
                File.WriteAllBytes(path, bytes);
                saved.Add(new AgentJobArtifact(path, patch.BaseCommitId, activity.Id, activity.CreatedAt.Value));
            }
        }

        return saved;
    }

    private static JulesJobState NewState(string prompt, bool continuation, JulesRunContext context, IAgentRunLog runLog)
    {
        return new JulesJobState(
            RemotePhase: JulesRemotePhase.Polling,
            PromptSha256: HashPrompt(prompt),
            ActivityBaseline: [],
            Continuation: continuation,
            SessionLockHeld: false,
            EventsLogPath: context.EventsLogPath ?? runLog.EventsPath,
            TextLogPath: context.TextLogPath ?? runLog.TextLogPath);
    }

    private static AgentRunResult BuildResult(
        JulesRunContext context,
        JulesJobState state,
        string output,
        string remotePhase,
        string? inputPrompt,
        IAgentRunLog runLog)
    {
        return new AgentRunResult(
            Agent: AgentFacade.JulesAgent,
            SessionId: state.SessionId ?? string.Empty,
            ExitCode: null,
            OutputText: output,
            RawOutput: string.Empty,
            RunId: context.JobId,
            EventsLogPath: state.EventsLogPath ?? runLog.EventsPath,
            TextLogPath: state.TextLogPath ?? runLog.TextLogPath,
            OutputKind: "final_response",
            SessionUrl: state.SessionUrl,
            InputPrompt: inputPrompt,
            RemotePhase: remotePhase,
            Artifacts: state.Artifacts);
    }

    private static AgentRunStartedInfo Started(AgentRunRequest request, string fileName, IReadOnlyList<string> arguments)
    {
        return new AgentRunStartedInfo(
            AgentFacade.JulesAgent,
            request.WorkingDirectory,
            request.SessionId,
            request.AutoApprove,
            request.Skills,
            request.Prompt,
            fileName,
            arguments);
    }

    internal static string HashPrompt(string prompt)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
    }

    private static bool SameCommit(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static JulesFailedOutcome Fail(Exception exception, string kind, bool releaseLock, string? sessionId)
    {
        if (exception.Data[CliJson.FailureKindKey] is not string)
        {
            CliJson.MarkFailure(exception, kind, exception.Message);
        }

        return new JulesFailedOutcome(exception, releaseLock, sessionId);
    }

    private static JulesRunRejectedException Reject(string kind, string message, bool releaseLock, string? sessionId)
    {
        var exception = new JulesRunRejectedException(kind, message, releaseLock, sessionId);
        CliJson.MarkFailure(exception, kind, message);
        return exception;
    }
}

internal sealed class NullAgentRunLog : IAgentRunLog
{
    public static NullAgentRunLog Instance { get; } = new();

    public string RunId => string.Empty;

    public string EventsPath => string.Empty;

    public string TextLogPath => string.Empty;

    public void WriteStarted(AgentRunStartedInfo info)
    {
    }

    public void WriteLaunch(ProcessLaunchInfo info)
    {
    }

    public void WriteAgentEvent(string type, JsonElement payload, string? humanSummary)
    {
    }

    public void AppendHumanFragment(string kind, string text)
    {
    }

    public void WriteProcessLine(string stream, string line)
    {
    }

    public void NoteExternalOutput()
    {
    }

    public void AttachProcess(IProcessLifetime process)
    {
    }

    public string? BoundSessionId => null;

    /// <summary>
    /// 共有 singleton は run ごとの identity を持てない。検証と永続化は <see cref="AgentRunLog"/> が行う。
    /// </summary>
    public void BindSession(string sessionId, string bindingSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingSource);
    }

    public void WriteCompleted(AgentRunResult result)
    {
    }

    public void WriteFailed(Exception exception)
    {
    }

    public void WriteCancelled()
    {
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class JulesRunRejectedException : Exception
{
    public JulesRunRejectedException(string kind, string message, bool releaseSessionLock, string? sessionId)
        : base(message)
    {
        Kind = kind;
        ReleaseSessionLock = releaseSessionLock;
        SessionId = sessionId;
    }

    public string Kind { get; }

    public bool ReleaseSessionLock { get; }

    public string? SessionId { get; }
}
