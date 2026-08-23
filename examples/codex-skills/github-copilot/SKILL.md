---
name: github-copilot
description: Forward the rest of the user message to GitHub Copilot CLI through the Codex Agent Facade MCP tool. Use when the user wants GitHub Copilot to do the coding work inside this Codex thread.
---

Call the MCP tool `run_agent` on server `codex_agent_facade`.

- `agent`: `github-copilot`
- `prompt`: the user's message after this skill, unchanged
- `working_directory`: the current Codex workspace / worktree path
- `session_id`: the previous `sessionId` from this thread's last `run_agent` result when continuing the same Copilot session
- `skills`: only the skill names the user asked to pass through, in Codex form

Do not replan, split, or rewrite the user's task. After the tool returns, show `outputText` to the user and keep `sessionId` for the next turn.
