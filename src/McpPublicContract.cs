/// <summary>
/// Codex へ公開する MCP instructions / tool schema の文言。
/// Facade は薄い transport のまま、caller が worker prompt を構成して委譲できる契約を固定する。
/// </summary>
public static class McpPublicContract
{
    public const string ServerInstructions =
        "Thin execution transport from Codex to GitHub Copilot, Grok Build, Cursor CLI, or Codex CLI. "
        + "The caller is responsible for deciding whether and how to delegate work and for constructing a self-contained prompt for the selected external agent. "
        + "The Facade itself does not plan, split, or semantically rewrite the worker prompt supplied to start_agent. "
        + "Do not interpret this as requiring the caller to forward the original user prompt. "
        + "The caller may derive a narrower worker-specific prompt from its own instructions and context. "
        + "Caller-supplied structured options such as skills may be translated by the selected driver into that agent's native invocation form. "
        + "Optional model is an agent-native base model id supplied by the caller. Its identity is never renamed or replaced by the Facade. "
        + "When it is omitted, empty, or whitespace, the Facade does not pass a model argument and does not select, rename, or fall back to another model. "
        + "Model names inside the worker prompt are not launch settings. "
        + "Optional reasoning_effort and fast are independent execution options, never inferred from the prompt. "
        + "When omitted, they leave the CLI's current behavior unchanged. "
        + "Cursor requires an explicit model for either option; independent fast is unsupported by GitHub Copilot and Grok Build and is rejected. "
        + "Call start_agent with request_id, agent, prompt, and working_directory. "
        + "Each start_agent call is a complete RPC; previous arguments are not retained. "
        + "Generate a new request_id for each distinct agent job. "
        + "A new user turn in the same Codex thread, or continuation of the same external session, still requires a new request_id. "
        + "Reuse the same request_id only when retrying the exact same start_agent request after its result may have been lost. "
        + "After start_agent, normally call wait_agent_job with only the returned jobId until completed, failed, or cancelled. "
        + "Omit timeout_seconds for the normal completion path to use the 300-second default; specify it only when diagnosis, testing, or another explicit constraint requires an override. "
        + "Do not use short-interval get_agent_job polling to wait out a long-running worker. "
        + "Use get_agent_job only for explicit status checks, recovery, or diagnosis. "
        + "wait_agent_job does not start, restart, or cancel the worker. "
        + "A completed job means only that the external CLI finished; review the reported response, diff, tests, and other evidence before accepting the work. "
        + "Distinguish failed jobs, lost responses, and wait timeouts. A recovery retry reuses the same request_id and must not start a second job. "
        + "Successful results expose outputKind as final_response or assistant_transcript; raw streams are available only in run logs. "
        + "Reuse session_id from a completed result to continue the same external agent session.";

    public const string StartAgentDescription =
        "Start a coding agent job (github-copilot, grok-build, cursor, or codex) and return a jobId immediately. "
        + "The caller constructs a self-contained worker prompt and a request_id for this distinct job. "
        + "Pass request_id, agent, prompt, and working_directory on every call. "
        + "Reuse that request_id only if this start_agent result is lost. "
        + "Then normally call wait_agent_job with only the returned jobId until the job is terminal. "
        + "Omit timeout_seconds to use the 300-second default unless diagnosis, testing, or another explicit constraint requires an override. "
        + "Completed means only CLI execution completed; the caller must review the response, diff, tests, and evidence before accepting the work. "
        + "A lost response is recovered with the same request_id; do not start a new job for recovery. "
        + "Successful results expose outputKind as final_response or assistant_transcript; raw streams are not returned by MCP. "
        + "The Facade does not plan, split, or semantically rewrite the supplied worker task. "
        + "Structured options such as skills may be translated by the selected driver. "
        + "When reasoning_effort and fast are omitted, the selected CLI receives the model id as supplied. Cursor encodes explicit execution options in its model parameter syntax without selecting another base model. "
        + "When model is omitted, the Facade does not pass a model argument and does not choose another model. "
        + "Model names inside the worker prompt are not launch settings. "
        + "Optional reasoning_effort is passed as a separate CLI setting; empty or unsafe values are rejected. "
        + "Optional fast is a tri-state boolean: omitted preserves existing behavior, true requests fast, and false requests the provider's explicit non-fast/default tier. "
        + "Cursor requires an explicit model to use reasoning_effort or fast. GitHub Copilot and Grok Build reject either explicit fast value because their CLIs provide no independent fast switch. "
        + "All settings must be specified again when continuing a session or retrying a start_agent request.";

    public const string GetAgentJobDescription =
        "Get the current status or terminal result of a previously started agent job. "
        + "Does not start, restart, or cancel work. "
        + "Use this for explicit status checks, recovery, or diagnosis. "
        + "A failed snapshot includes a stable failure object when available; it is distinct from a wait timeout or a lost response. "
        + "Do not poll this tool at a short interval to wait for a long-running worker; use wait_agent_job instead.";

    public const string WaitAgentJobDescription =
        "Wait until a previously started agent job is completed, failed, or cancelled, or until timeout_seconds elapses. Normally omit timeout_seconds and use wait_agent_job(job_id); the Facade then applies a 300-second upper bound. "
        + "Specify timeout_seconds only for diagnosis, testing, or another explicit constraint; an explicit value is not replaced by the default. "
        + "Does not start, restart, or cancel the worker. "
        + "If the job is already terminal, return immediately. "
        + "On timeout, return the current running snapshot and leave the worker running. "
        + "Cancelling this wait does not cancel the worker; use cancel_agent_job to stop it. "
        + "Do not repeat short waits as the normal completion path.";

    public const string WaitTimeoutSecondsDescription =
        "Optional maximum seconds to wait for a terminal job state. Normally omit this argument; the Facade uses 300 seconds. "
        + "Specify it only for diagnosis, testing, or another explicit constraint. When provided, it must be an integer from 1 to 86400 and is not replaced by the default. "
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
        + "Exact delivery of this string is not guaranteed; the selected driver may add agent-native skill directives when skills are supplied. "
        + "Model names written in this text are not launch settings; pass model separately to select one.";

    public const string SkillsDescription =
        "Optional Codex-format skill names. GitHub Copilot and Grok Build translate them to agent-native prompt directives. "
        + "Cursor and Codex currently do not translate this field; explicit skill invocation must be included in the worker prompt.";

    public const string AutoApproveDescription =
        "When true (default), allow the selected CLI to make its normal workspace changes without interactive approval. "
        + "When false, use the CLI's non-interactive restricted mode. For Codex this means read-only sandbox with approval_policy=never, so approval questions are not shown.";

    public const string ModelDescription =
        "Optional agent-native model id. When omitted, empty, or whitespace, the Facade does not pass a model argument and does not choose, rename, or fall back to another model. "
        + "When set, the value is forwarded unchanged as the selected CLI model. Cursor appends explicitly requested reasoning_effort/fast parameters using its documented parameterized-model syntax; it does not choose a different model. "
        + "Model names that appear only inside the worker prompt are not launch settings. "
        + "Line breaks are rejected before launch; other control characters are rejected for a non-blank model value. Blank values retain the existing omitted-model behavior. "
        + "Pass the same value again on an exact retry. Continuing a session does not inherit a previous model.";

    public const string ReasoningEffortDescription =
        "Optional provider-native reasoning effort token, separate from the worker prompt. Omit it to preserve the CLI default. "
        + "GitHub Copilot accepts none, minimal, low, medium, high, xhigh, or max; Grok Build accepts its CLI canonical or model-specific effort tokens; Cursor accepts only values supported by the selected model. "
        + "Cursor requires model. Copilot values are none, minimal, low, medium, high, xhigh, or max; Grok Build may accept model-specific tokens. Unsupported values or model combinations must fail rather than silently change effort. "
        + "Specify it again when continuing a session or retrying the exact request.";

    public const string FastDescription =
        "Optional explicit fast tier. Omit to preserve the CLI's existing behavior; true requests fast; false explicitly requests the provider's default/non-fast tier. "
        + "Cursor and Codex support this independent option. GitHub Copilot and Grok Build reject either explicit value because their CLIs expose no independent fast switch. "
        + "Cursor requires model. A fast model id may still be selected through model alone. Do not infer fast from model text or substitute another model. Specify the same value on an exact retry.";
}
