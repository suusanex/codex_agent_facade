/// <summary>
/// Codex へ公開する MCP instructions / tool schema の文言。
/// Facade は薄い transport のまま、caller が worker prompt を構成して委譲できる契約を固定する。
/// </summary>
public static class McpPublicContract
{
    public const string ServerInstructions =
        "Thin execution transport from Codex to GitHub Copilot, Grok Build, Devin CLI, or Cursor CLI. "
        + "The caller is responsible for deciding whether and how to delegate work and for constructing a self-contained prompt for the selected external agent. "
        + "The Facade itself does not plan, split, reinterpret, or rewrite the prompt supplied to start_agent; it forwards that supplied worker prompt unchanged. "
        + "Do not interpret this as requiring the caller to forward the original user prompt. "
        + "The caller may derive a narrower worker-specific prompt from its own instructions and context. "
        + "Call start_agent with request_id, agent, prompt, and working_directory. "
        + "Generate a new request_id for each distinct agent job. "
        + "Reuse the same request_id only when retrying the exact same start_agent request after its result may have been lost. "
        + "Poll get_agent_job with the returned jobId until completed, failed, or cancelled. "
        + "Reuse session_id from a completed result to continue the same external agent session.";

    public const string StartAgentDescription =
        "Start a coding agent job (github-copilot, grok-build, devin-cli, or cursor) and return a jobId immediately. "
        + "The caller constructs a self-contained worker prompt and a request_id for this distinct job. "
        + "Reuse that request_id only if this start_agent result is lost. Poll get_agent_job. "
        + "The Facade does not plan, split, or rewrite the supplied worker prompt.";

    public const string RequestIdDescription =
        "Caller-generated idempotency key for this distinct agent job. "
        + "Generate a new value for each distinct start_agent job. "
        + "Reuse the exact same value only to recover a lost start_agent result without starting a second agent.";

    public const string PromptDescription =
        "Self-contained worker prompt constructed by the caller and forwarded unchanged to the selected external agent. "
        + "It need not be the original user prompt.";
}
