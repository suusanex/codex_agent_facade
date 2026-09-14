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
        + "Poll get_agent_job with the returned jobId until completed, failed, or cancelled. "
        + "Reuse session_id from a completed result to continue the same external agent session.";

    public const string StartAgentDescription =
        "Start a coding agent job (github-copilot, grok-build, devin-cli, or cursor) and return a jobId immediately. "
        + "The caller constructs a self-contained worker prompt and a request_id for this distinct job. "
        + "Pass request_id, agent, prompt, and working_directory on every call. "
        + "Reuse that request_id only if this start_agent result is lost. Poll get_agent_job. "
        + "The Facade does not plan, split, or semantically rewrite the supplied worker task. "
        + "Structured options such as skills may be translated by the selected driver.";

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
