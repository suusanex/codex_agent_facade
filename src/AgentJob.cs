using System.Security;
using System.Text;
using System.Text.Json;

/// <summary>
/// job の公開状態。MCP Tasks の working/completed/failed/cancelled に寄せるが、wire は独自 JSON。
/// </summary>
public static class AgentJobStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";

    /// <summary>
    /// Jules が追加入力を待っている settled 状態。provider session の終了ではない。
    /// </summary>
    public const string WaitingForInput = "waiting_for_input";
}

/// <summary>
/// Jules が返した patch を改変せず保存した公開成果。増分か累積かは決め打ちしない。
/// </summary>
public sealed record AgentJobArtifact(
    string PatchPath,
    string BaseCommitId,
    string ActivityId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Jules job の再開に必要な最小状態。API key と prompt 本文は入れない。
/// </summary>
public sealed record JulesJobState(
    string RemotePhase,
    string PromptSha256,
    IReadOnlyList<string> ActivityBaseline,
    bool Continuation,
    bool SessionLockHeld,
    string? SessionId = null,
    string? SessionUrl = null,
    string? InputPrompt = null,
    string? SourceName = null,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    string? StartingBranch = null,
    IReadOnlyList<AgentJobArtifact>? Artifacts = null,
    string? EventsLogPath = null,
    string? TextLogPath = null);

public static class JulesRemotePhase
{
    public const string SubmittingCreate = "submitting_create";
    public const string SubmittingMessage = "submitting_message";
    public const string Polling = "polling";
    public const string SubmissionUnknown = "submission_unknown";
    public const string AwaitingUserFeedback = "awaiting_user_feedback";
    public const string Completed = "completed";
    public const string CommunicationError = "communication_error";
}

/// <summary>
/// start/get/cancel/wait が内部で扱う job snapshot。実行中は result を持たない。
/// MCP 公開 JSON は <see cref="AgentJobPublicSnapshot"/> を使う。
/// </summary>
public static class AgentSessionRecoveryStatus
{
    public const string Bound = "bound";
    public const string Unavailable = "unavailable";
}

public static class AgentSessionRecoveryReason
{
    public const string RunLogMissing = "run_log_missing";
    public const string RunLogUnreadable = "run_log_unreadable";
    public const string RunLogCorrupt = "run_log_corrupt";
    public const string RunLogUnavailable = "run_log_unavailable";
    public const string SessionMismatch = "session_mismatch";
}

/// <summary>
/// 失敗・取消となった local CLI job の継続用 session 復元状態。
/// ID 未確定時は null として公開 JSON から省略し、欠落・読取不能・破損と区別する。
/// </summary>
public sealed record AgentSessionRecovery(
    string Status,
    string? SessionId = null,
    string? BindingSource = null,
    string? Reason = null);

public sealed record AgentJobSnapshot(
    string JobId,
    string RequestId,
    string Status,
    AgentRunResult? Result,
    string? Error,
    AgentJobFailure? Failure,
    string? SessionId = null,
    string? SessionUrl = null,
    string? InputPrompt = null,
    string? RemotePhase = null,
    IReadOnlyList<AgentJobArtifact>? Artifacts = null,
    JulesJobState? Jules = null,
    AgentSessionRecovery? Recovery = null);

/// <summary>
/// MCP 公開用の job snapshot。内部の CLI raw output と Jules の相関 cursor は含めない。
/// </summary>
public sealed record AgentJobPublicSnapshot(
    string JobId,
    string RequestId,
    string Status,
    AgentRunPublicResult? Result,
    string? Error,
    AgentJobFailure? Failure,
    string? SessionId = null,
    string? SessionUrl = null,
    string? InputPrompt = null,
    string? RemotePhase = null,
    IReadOnlyList<AgentJobArtifact>? Artifacts = null,
    AgentSessionRecovery? Recovery = null);

/// <summary>
/// MCP 公開用の terminal result。Driver が抽出した outputText と run log 識別情報だけを返す。
/// </summary>
public sealed record AgentRunPublicResult(
    string Agent,
    string SessionId,
    int? ExitCode,
    string OutputText,
    string RunId,
    string EventsLogPath,
    string TextLogPath,
    string OutputKind = "assistant_transcript",
    string? CompletionReason = null,
    GrokCompletionEvidence? CompletionEvidence = null,
    ProcessTerminationInfo? ProcessTermination = null,
    string? PlanPath = null,
    string? SessionUrl = null,
    string? InputPrompt = null,
    string? RemotePhase = null,
    IReadOnlyList<AgentJobArtifact>? Artifacts = null);

/// <summary>
/// 失敗時に親が復旧判断へ使う、公開可能な最小限の診断情報。
/// </summary>
public sealed record AgentJobFailure(
    string Kind,
    string Summary,
    int? ExitCode = null,
    string? RunId = null,
    string? EventsLogPath = null,
    string? TextLogPath = null);

internal static class AgentJobPublicProjection
{
    public static AgentJobPublicSnapshot From(AgentJobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = snapshot.Result is null ? null : From(snapshot.Result);
        // 既存 agent の result.sessionId を snapshot 直下へ複製しない。Jules の進行と成果だけを足す。
        var publishRemote = snapshot.Jules is not null
            || string.Equals(snapshot.Result?.Agent, AgentFacade.JulesAgent, StringComparison.Ordinal)
            || snapshot.SessionUrl is not null
            || snapshot.InputPrompt is not null
            || snapshot.RemotePhase is not null
            || snapshot.Artifacts is not null
            || result?.SessionUrl is not null
            || result?.InputPrompt is not null
            || result?.RemotePhase is not null
            || result?.Artifacts is not null;
        return new AgentJobPublicSnapshot(
            snapshot.JobId,
            snapshot.RequestId,
            snapshot.Status,
            result,
            snapshot.Error,
            snapshot.Failure,
            publishRemote ? snapshot.SessionId ?? result?.SessionId : null,
            publishRemote ? snapshot.SessionUrl ?? result?.SessionUrl : null,
            publishRemote ? snapshot.InputPrompt ?? result?.InputPrompt : null,
            publishRemote ? snapshot.RemotePhase ?? result?.RemotePhase : null,
            publishRemote ? snapshot.Artifacts ?? result?.Artifacts : null,
            ProjectRecovery(snapshot.Status, publishRemote, snapshot.Recovery));
    }

    internal static AgentSessionRecovery? ProjectRecovery(
        string status,
        bool isRemote,
        AgentSessionRecovery? recovery)
    {
        if (isRemote || recovery is null)
        {
            return null;
        }

        return status is AgentJobStatus.Failed or AgentJobStatus.Cancelled or AgentJobStatus.TimedOut
            ? recovery
            : null;
    }

    public static AgentRunPublicResult From(AgentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AgentRunPublicResult(
            result.Agent,
            result.SessionId,
            result.ExitCode,
            result.OutputText,
            result.RunId,
            result.EventsLogPath,
            result.TextLogPath,
            result.OutputKind,
            result.CompletionReason,
            result.CompletionEvidence,
            result.ProcessTermination,
            result.PlanPath,
            result.SessionUrl,
            result.InputPrompt,
            result.RemotePhase,
            result.Artifacts);
    }
}

/// <summary>
/// 1 件の agent job。MCP request の lifetime とは独立した CTS を持つ。
/// </summary>
public sealed class AgentJob
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _status = AgentJobStatus.Running;
    private bool _terminalPersisted;
    private AgentRunResult? _result;
    private string? _error;
    private AgentJobFailure? _failure;
    private string? _sessionId;
    private string? _sessionUrl;
    private string? _inputPrompt;
    private string? _remotePhase;
    private IReadOnlyList<AgentJobArtifact>? _artifacts;
    private JulesJobState? _jules;
    private IAgentRunLog? _activeRunLog;
    private string? _eventsLogPath;
    private SessionBindingInfo? _boundSession;
    private AgentSessionRecovery? _recovery;
    private bool _recoveryFinalized;
    private int _monitorStarted;

    public AgentJob(
        string jobId,
        string requestId,
        AgentRunRequest request,
        DateTimeOffset createdAt,
        string? boundFingerprint = null,
        JulesJobState? jules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);
        JobId = jobId;
        RequestId = requestId;
        Request = request;
        CreatedAt = createdAt;
        BoundFingerprint = boundFingerprint;
        if (jules is not null)
        {
            _jules = jules;
            _sessionId = jules.SessionId;
            _sessionUrl = jules.SessionUrl;
            _inputPrompt = jules.InputPrompt;
            _remotePhase = jules.RemotePhase;
            _artifacts = jules.Artifacts;
        }
    }

    public string JobId { get; }

    public string RequestId { get; }

    public AgentRunRequest Request { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// 再起動後に再構成した job の request 比較用。通常起動では null で、引数そのものを比較する。
    /// </summary>
    public string? BoundFingerprint { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    internal string? EventsLogPath
    {
        get
        {
            lock (_gate)
            {
                return _eventsLogPath;
            }
        }
    }

    internal bool RecoveryFinalized
    {
        get
        {
            lock (_gate)
            {
                return _recoveryFinalized;
            }
        }
    }

    internal void AttachRunLog(IAgentRunLog log, Action<AgentJobSnapshot>? persist = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (_gate)
        {
            var previousLog = _activeRunLog;
            var previousPath = _eventsLogPath;
            _activeRunLog = log;
            if (!string.IsNullOrWhiteSpace(log.EventsPath))
            {
                _eventsLogPath = log.EventsPath;
            }

            if (persist is not null && !IsTerminalStatus(_status))
            {
                try
                {
                    persist(CreateCurrentSnapshotUnlocked());
                }
                catch
                {
                    _activeRunLog = previousLog;
                    _eventsLogPath = previousPath;
                    throw;
                }
            }
        }
    }

    internal void RecordBoundSession(SessionBindingInfo binding, Action<AgentJobSnapshot>? persist = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            if (_jules is not null
                || string.Equals(Request.Agent.Trim(), AgentFacade.JulesAgent, StringComparison.Ordinal))
            {
                return;
            }

            var previousBound = _boundSession;
            var previousRecovery = _recovery;
            var effective = previousBound ?? binding;
            var nextRecovery = new AgentSessionRecovery(
                AgentSessionRecoveryStatus.Bound,
                effective.SessionId,
                string.IsNullOrWhiteSpace(effective.BindingSource) ? null : effective.BindingSource);

            if (!IsTerminalStatus(_status))
            {
                if (_boundSession is not null && _recovery == nextRecovery)
                {
                    return;
                }

                try
                {
                    _boundSession ??= binding;
                    _recovery = nextRecovery;
                    persist?.Invoke(CreateCurrentSnapshotUnlocked());
                }
                catch
                {
                    _boundSession = previousBound;
                    _recovery = previousRecovery;
                    throw;
                }

                return;
            }

            if (_status is AgentJobStatus.Failed or AgentJobStatus.Cancelled
                && _recovery?.Status != AgentSessionRecoveryStatus.Bound)
            {
                try
                {
                    _boundSession ??= binding;
                    _recovery = nextRecovery;
                    persist?.Invoke(CreateCurrentSnapshotUnlocked());
                    _terminalPersisted = true;
                }
                catch
                {
                    _boundSession = previousBound;
                    _recovery = previousRecovery;
                    throw;
                }
            }
        }
    }

    internal void FinalizeLocalRun(Action<AgentJobSnapshot>? persist = null)
    {
        lock (_gate)
        {
            if (_jules is not null
                || string.Equals(Request.Agent.Trim(), AgentFacade.JulesAgent, StringComparison.Ordinal))
            {
                return;
            }

            if (!IsTerminalStatus(_status))
            {
                return;
            }

            var boundSession = _boundSession ?? _activeRunLog?.BoundSession;
            AgentSessionRecovery? nextRecovery;
            if (_status == AgentJobStatus.Completed)
            {
                nextRecovery = null;
            }
            else if (boundSession is not null)
            {
                nextRecovery = new AgentSessionRecovery(
                    AgentSessionRecoveryStatus.Bound,
                    boundSession.SessionId,
                    string.IsNullOrWhiteSpace(boundSession.BindingSource) ? null : boundSession.BindingSource);
            }
            else if (_recovery?.Status == AgentSessionRecoveryStatus.Bound)
            {
                nextRecovery = _recovery;
            }
            else if (!string.IsNullOrWhiteSpace(_eventsLogPath))
            {
                // log 終了時点で session_bound が無ければ、欠落・読取不能・破損等の診断結果ごと確定させる。
                nextRecovery = AgentSessionLogRecovery.ReadFromEventsLog(_eventsLogPath);
                if (nextRecovery is { Status: AgentSessionRecoveryStatus.Bound }
                    && !string.IsNullOrWhiteSpace(nextRecovery.SessionId)
                    && !string.IsNullOrWhiteSpace(nextRecovery.BindingSource))
                {
                    boundSession = new SessionBindingInfo(nextRecovery.SessionId, nextRecovery.BindingSource);
                }
            }
            else
            {
                nextRecovery = _recovery;
            }

            if (_recoveryFinalized && _recovery == nextRecovery && _terminalPersisted)
            {
                return;
            }

            var previousBound = _boundSession;
            var previousRecovery = _recovery;
            var previousFinalized = _recoveryFinalized;
            try
            {
                _boundSession ??= boundSession;
                _recovery = nextRecovery;
                _recoveryFinalized = true;
                persist?.Invoke(CreateCurrentSnapshotUnlocked());
                _terminalPersisted = true;
            }
            catch
            {
                _boundSession = previousBound;
                _recovery = previousRecovery;
                _recoveryFinalized = previousFinalized;
                throw;
            }
        }
    }

    public JulesJobState? Jules
    {
        get
        {
            lock (_gate)
            {
                return _jules;
            }
        }
    }

    /// <summary>
    /// terminal 遷移で完了する。waiter の CT とは独立しており、job 自体は cancel しない。
    /// </summary>
    public Task WhenTerminal => _terminal.Task;

    public bool IsTerminal
    {
        get
        {
            lock (_gate)
            {
                return IsTerminalStatus(_status);
            }
        }
    }

    /// <summary>
    /// terminal 状態の保存コールバックが成功したときだけ true。
    /// 未保存のまま登録を外すと、disk の running を facade_interrupted として上書きする。
    /// </summary>
    public bool TerminalPersisted
    {
        get
        {
            lock (_gate)
            {
                return _terminalPersisted;
            }
        }
    }

    public bool RequestCancel(Action<AgentJobSnapshot>? beforePublish = null)
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }

        return TryFinish(AgentJobStatus.Cancelled, result: null, error: "cancelled", failure: null, beforePublish);
    }

    public bool Complete(AgentRunResult result, Action<AgentJobSnapshot>? beforePublish = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TryFinish(AgentJobStatus.Completed, result, error: null, failure: null, beforePublish);
    }

    public bool SettleWaiting(AgentRunResult result, Action<AgentJobSnapshot>? beforePublish = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TryFinish(AgentJobStatus.WaitingForInput, result, error: null, failure: null, beforePublish);
    }

    /// <summary>
    /// running のまま Jules の session と相関状態を公開する。terminal にはしない。
    /// </summary>
    public void UpdateJules(JulesJobState state, Action<AgentJobSnapshot> persist)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(persist);
        AgentJobSnapshot projected;
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
            {
                return;
            }

            projected = CreateSnapshotUnlocked(
                _status,
                _result,
                _error,
                _failure,
                state.SessionId ?? _sessionId,
                state.SessionUrl ?? _sessionUrl,
                state.InputPrompt ?? _inputPrompt,
                state.RemotePhase,
                state.Artifacts ?? _artifacts,
                state,
                _recovery);
        }

        persist(projected);
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
            {
                return;
            }

            _jules = state;
            _sessionId = state.SessionId ?? _sessionId;
            _sessionUrl = state.SessionUrl ?? _sessionUrl;
            _inputPrompt = state.InputPrompt ?? _inputPrompt;
            _remotePhase = state.RemotePhase;
            _artifacts = state.Artifacts ?? _artifacts;
        }
    }

    public bool TryBeginMonitor()
    {
        return Interlocked.CompareExchange(ref _monitorStarted, 1, 0) == 0;
    }

    public void EndMonitorIfStillRunning()
    {
        if (!IsTerminal)
        {
            Interlocked.Exchange(ref _monitorStarted, 0);
        }
    }

    public bool Fail(string error, AgentJobFailure? failure = null, Action<AgentJobSnapshot>? beforePublish = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return TryFinish(AgentJobStatus.Failed, result: null, error, failure, beforePublish);
    }

    public bool MarkCancelled(Action<AgentJobSnapshot>? beforePublish = null)
    {
        return TryFinish(AgentJobStatus.Cancelled, result: null, error: "cancelled", failure: null, beforePublish);
    }

    public AgentJobSnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            return CreateCurrentSnapshotUnlocked();
        }
    }

    private AgentJobSnapshot CreateCurrentSnapshotUnlocked()
    {
        return CreateSnapshotUnlocked(
            _status,
            _result,
            _error,
            _failure,
            _sessionId,
            _sessionUrl,
            _inputPrompt,
            _remotePhase,
            _artifacts,
            _jules,
            _recovery);
    }

    public void Discard()
    {
        try
        {
            _cancellation.Dispose();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }

    /// <summary>
    /// terminal 状態を公開する前に <paramref name="beforePublish"/> を実行する。
    /// Get と WhenTerminal はロック解放とシグナルの後でだけ新しい状態を見る。
    /// 先に公開すると、disk が running のまま再読込され facade_interrupted になる。
    /// beforePublish が失敗したときは状態を変えず、例外を返す。
    /// </summary>
    private bool TryFinish(
        string status,
        AgentRunResult? result,
        string? error,
        AgentJobFailure? failure,
        Action<AgentJobSnapshot>? beforePublish)
    {
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
            {
                return false;
            }

            // 既存 agent の公開 JSON に sessionId を重複させない。Jules の進行と成果だけを足す。
            var publishRemote = _jules is not null
                || string.Equals(Request.Agent.Trim(), AgentFacade.JulesAgent, StringComparison.Ordinal)
                || string.Equals(result?.Agent, AgentFacade.JulesAgent, StringComparison.Ordinal)
                || result?.SessionUrl is not null
                || result?.InputPrompt is not null
                || result?.RemotePhase is not null
                || result?.Artifacts is not null;
            var sessionId = publishRemote ? result?.SessionId ?? _sessionId : _sessionId;
            var sessionUrl = publishRemote ? result?.SessionUrl ?? _sessionUrl : _sessionUrl;
            var inputPrompt = publishRemote ? result?.InputPrompt ?? _inputPrompt : _inputPrompt;
            var remotePhase = publishRemote ? result?.RemotePhase ?? _remotePhase : _remotePhase;
            var artifacts = publishRemote ? result?.Artifacts ?? _artifacts : _artifacts;
            var jules = _jules is null
                ? null
                : _jules with
                {
                    SessionId = sessionId ?? _jules.SessionId,
                    SessionUrl = sessionUrl ?? _jules.SessionUrl,
                    InputPrompt = inputPrompt ?? _jules.InputPrompt,
                    RemotePhase = remotePhase ?? _jules.RemotePhase,
                    Artifacts = artifacts ?? _jules.Artifacts,
                };

            SessionBindingInfo? boundSession = null;
            AgentSessionRecovery? recovery = null;
            var recoveryFinalized = _recoveryFinalized;
            if (publishRemote || status == AgentJobStatus.Completed)
            {
                recovery = null;
                recoveryFinalized = status == AgentJobStatus.Completed;
            }
            else if (status is AgentJobStatus.Failed or AgentJobStatus.Cancelled)
            {
                boundSession = _boundSession ?? _activeRunLog?.BoundSession;
                if (boundSession is not null)
                {
                    recovery = new AgentSessionRecovery(
                        AgentSessionRecoveryStatus.Bound,
                        boundSession.SessionId,
                        string.IsNullOrWhiteSpace(boundSession.BindingSource) ? null : boundSession.BindingSource);
                }
                else if (_recovery?.Status == AgentSessionRecoveryStatus.Bound)
                {
                    recovery = _recovery;
                }
                else if (status == AgentJobStatus.Cancelled
                    && _activeRunLog is not null
                    && !string.IsNullOrWhiteSpace(_eventsLogPath))
                {
                    // Cancel が BindSession より先行した場合も、flush 済み session_bound やログ障害診断をそのまま保持する。
                    recovery = AgentSessionLogRecovery.ReadFromEventsLog(_eventsLogPath);
                    if (recovery is { Status: AgentSessionRecoveryStatus.Bound }
                        && !string.IsNullOrWhiteSpace(recovery.SessionId)
                        && !string.IsNullOrWhiteSpace(recovery.BindingSource))
                    {
                        boundSession = new SessionBindingInfo(recovery.SessionId, recovery.BindingSource);
                    }
                }
            }

            if (beforePublish is not null)
            {
                var previousBound = _boundSession;
                var previousRecovery = _recovery;
                var previousFinalized = _recoveryFinalized;
                try
                {
                    _boundSession ??= boundSession;
                    _recovery = recovery;
                    _recoveryFinalized = recoveryFinalized;
                    beforePublish.Invoke(CreateSnapshotUnlocked(
                        status,
                        result,
                        error,
                        failure,
                        sessionId,
                        sessionUrl,
                        inputPrompt,
                        remotePhase,
                        artifacts,
                        jules,
                        recovery));
                    _terminalPersisted = true;
                }
                catch
                {
                    _boundSession = previousBound;
                    _recovery = previousRecovery;
                    _recoveryFinalized = previousFinalized;
                    throw;
                }
            }

            _status = status;
            _result = result;
            _error = error;
            _failure = failure;
            _sessionId = sessionId;
            _sessionUrl = sessionUrl;
            _inputPrompt = inputPrompt;
            _remotePhase = remotePhase;
            _artifacts = artifacts;
            _jules = jules;
            _boundSession ??= boundSession;
            _recovery = recovery;
            _recoveryFinalized = recoveryFinalized;
        }

        _terminal.TrySetResult();
        return true;
    }

    private AgentJobSnapshot CreateSnapshotUnlocked(
        string status,
        AgentRunResult? result,
        string? error,
        AgentJobFailure? failure,
        string? sessionId,
        string? sessionUrl,
        string? inputPrompt,
        string? remotePhase,
        IReadOnlyList<AgentJobArtifact>? artifacts,
        JulesJobState? jules,
        AgentSessionRecovery? recovery)
    {
        return new AgentJobSnapshot(
            JobId,
            RequestId,
            status,
            result,
            error,
            failure,
            sessionId,
            sessionUrl,
            inputPrompt,
            remotePhase,
            artifacts,
            jules,
            recovery);
    }

    private static bool IsTerminalStatus(string status)
    {
        return status is AgentJobStatus.Completed
            or AgentJobStatus.Failed
            or AgentJobStatus.Cancelled
            or AgentJobStatus.WaitingForInput;
    }
}

/// <summary>
/// events.jsonl から <c>session_bound</c> を復元する補助。
/// ID 未確定（破損なし・イベントなし）と、欠落・読取不能・破損・不整合を区別する。
/// </summary>
internal static class AgentSessionLogRecovery
{
    public static AgentSessionRecovery? ReadFromEventsLog(string eventsPath)
    {
        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            return new AgentSessionRecovery(
                AgentSessionRecoveryStatus.Unavailable,
                Reason: AgentSessionRecoveryReason.RunLogUnavailable);
        }

        try
        {
            if (!File.Exists(eventsPath))
            {
                return new AgentSessionRecovery(
                    AgentSessionRecoveryStatus.Unavailable,
                    Reason: AgentSessionRecoveryReason.RunLogMissing);
            }

            using var stream = new FileStream(
                eventsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            SessionBindingInfo? firstBound = null;
            var sawCorruptLine = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        sawCorruptLine = true;
                        continue;
                    }

                    var source = CliJson.TryGetPropertyIgnoreCase(root, "source", out var sourceElement)
                        ? CliJson.ReadStringValue(sourceElement)
                        : null;
                    var type = CliJson.TryGetPropertyIgnoreCase(root, "type", out var typeElement)
                        ? CliJson.ReadStringValue(typeElement)
                        : null;
                    if (!string.Equals(source, "facade", StringComparison.Ordinal)
                        || !string.Equals(type, "session_bound", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!CliJson.TryGetPropertyIgnoreCase(root, "data", out var dataElement)
                        || dataElement.ValueKind != JsonValueKind.Object)
                    {
                        sawCorruptLine = true;
                        continue;
                    }

                    var sessionId = CliJson.FindExplicitSessionId(dataElement);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        sawCorruptLine = true;
                        continue;
                    }

                    if (!CliJson.TryGetPropertyIgnoreCase(dataElement, "bindingSource", out var bindingElement)
                        || bindingElement.ValueKind != JsonValueKind.String)
                    {
                        sawCorruptLine = true;
                        continue;
                    }

                    var bindingSource = bindingElement.GetString();
                    if (string.IsNullOrWhiteSpace(bindingSource))
                    {
                        sawCorruptLine = true;
                        continue;
                    }

                    if (firstBound is null)
                    {
                        firstBound = new SessionBindingInfo(sessionId, bindingSource);
                    }
                    else if (!string.Equals(firstBound.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        return new AgentSessionRecovery(
                            AgentSessionRecoveryStatus.Unavailable,
                            Reason: AgentSessionRecoveryReason.SessionMismatch);
                    }
                }
                catch (JsonException ex)
                {
                    CliJson.TraceException(ex);
                    sawCorruptLine = true;
                }
            }

            if (firstBound is not null)
            {
                return new AgentSessionRecovery(
                    AgentSessionRecoveryStatus.Bound,
                    firstBound.SessionId,
                    firstBound.BindingSource);
            }

            if (sawCorruptLine)
            {
                return new AgentSessionRecovery(
                    AgentSessionRecoveryStatus.Unavailable,
                    Reason: AgentSessionRecoveryReason.RunLogCorrupt);
            }

            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            CliJson.TraceException(ex);
            return new AgentSessionRecovery(
                AgentSessionRecoveryStatus.Unavailable,
                Reason: AgentSessionRecoveryReason.RunLogMissing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            CliJson.TraceException(ex);
            return new AgentSessionRecovery(
                AgentSessionRecoveryStatus.Unavailable,
                Reason: AgentSessionRecoveryReason.RunLogUnreadable);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            return new AgentSessionRecovery(
                AgentSessionRecoveryStatus.Unavailable,
                Reason: AgentSessionRecoveryReason.RunLogUnavailable);
        }
    }
}
