/// <summary>
/// Codex へ公開する MCP instructions / tool schema の文言。
/// Facade は薄い transport のまま、caller が worker prompt を構成して委譲できる契約を固定する。
/// </summary>
public static class McpPublicContract
{
    public const string ServerInstructions =
        "Thin execution transport from Codex to GitHub Copilot, Grok Build, Devin CLI, or Cursor CLI. "
        + "The caller is responsible for deciding whether and how to delegate work and for constructing a self-contained prompt for the selected external agent. "
        + "The Facade itself does not plan, split, or semantically rewrite the worker prompt supplied to start_agent. "
        + "Do not interpret this as requiring the caller to forward the original user prompt. "
        + "The caller may derive a narrower worker-specific prompt from its own instructions and context. "
        + "Caller-supplied structured options such as skills may be translated by the selected driver into that agent's native invocation form. "
        + "Call start_agent with request_id, agent, prompt, and working_directory. "
        + "Each start_agent call is a complete RPC; previous arguments are not retained. "
        + "Generate a new request_id for each distinct agent job. "
        + "A new user turn in the same Codex thread, or continuation of the same external session, still requires a new request_id. "
        + "Reuse the same request_id only when retrying the exact same start_agent request after its result may have been lost. "
        + "After start_agent, call wait_agent_job with the returned jobId until completed, failed, or cancelled. "
        + "Do not use short-interval get_agent_job polling to wait out a long-running worker. "
        + "Use get_agent_job only for explicit status checks, recovery, or diagnosis. "
        + "wait_agent_job does not start, restart, or cancel the worker. "
        + "Reuse session_id from a completed result to continue the same external agent session.";

    public const string StartAgentDescription =
        "Start a coding agent job (github-copilot, grok-build, devin-cli, or cursor) and return a jobId immediately. "
        + "The caller constructs a self-contained worker prompt and a request_id for this distinct job. "
        + "Pass request_id, agent, prompt, and working_directory on every call. "
        + "Reuse that request_id only if this start_agent result is lost. "
        + "Then call wait_agent_job with the returned jobId until the job is terminal. "
        + "The Facade does not plan, split, or semantically rewrite the supplied worker task. "
        + "Structured options such as skills may be translated by the selected driver.";

    public const string GetAgentJobDescription =
        "Get the current status or terminal result of a previously started agent job. "
        + "Does not start, restart, or cancel work. "
        + "Use this for explicit status checks, recovery, or diagnosis. "
        + "Do not poll this tool at a short interval to wait for a long-running worker; use wait_agent_job instead.";

    public const string WaitAgentJobDescription =
        "Wait until a previously started agent job is completed, failed, or cancelled, or until timeout_seconds elapses. "
        + "Does not start, restart, or cancel the worker. "
        + "If the job is already terminal, return immediately. "
        + "On timeout, return the current running snapshot and leave the worker running. "
        + "Cancelling this wait does not cancel the worker; use cancel_agent_job to stop it. "
        + "A practical timeout_seconds value is 300.";

    public const string WaitTimeoutSecondsDescription =
        "Maximum seconds to wait for a terminal job state. "
        + "Must be an integer from 1 to 86400. "
        + "A practical value is 300 so that multi-minute workers can complete in one wait. "
        + "Timeout returns the current snapshot without cancelling the worker.";

    public const string RequestIdDescription =
        "Caller-generated idempotency key for this distinct agent job. "
        + "Generate a new value for each distinct start_agent job. "
        + "Do not reuse it merely because the Codex thread or external agent session is the same. "
        + "Reuse the exact same value only to recover a lost start_agent result without starting a second agent.";

    public const string WorkingDirectoryDescription =
        "Working directory or worktree for the agent process. "
        + "Required on every start_agent call, including continuations. Previous values are not retained.";

    public const string PromptDescription =
        "Self-contained worker prompt constructed by the caller. "
        + "The Facade does not reinterpret this task payload. It need not be the original user prompt. "
        + "Exact delivery of this string is not guaranteed; the selected driver may add agent-native skill directives when skills are supplied.";

    public const string SkillsDescription =
        "Optional Codex-format skill names. GitHub Copilot, Grok Build, and Devin CLI translate them to agent-native prompt directives. "
        + "Cursor currently does not translate this field; explicit Cursor skill invocation must be included in the worker prompt.";
}
