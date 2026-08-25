---
name: github-copilot
description: Codex 上のユーザー本文を Codex Agent Facade の MCP tool run_agent 経由で GitHub Copilot CLI へ渡す。この Codex thread で GitHub Copilot に実装させたいときに使う。
user-invocable: true
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: MIT
# License: https://opensource.org/licenses/MIT
# Source: https://github.com/suusanex/codex_agent_facade
---

# GitHub Copilot（Codex Agent Facade）

MCP tool `run_agent` を server `codex_agent_facade` で呼ぶ。

MCP server はこの Skill の一部ではない。ユーザーの Codex MCP 設定（publish した Facade 実行ファイルを `~/.codex/config.toml` に書く）が必要である。この Skill は server の導入・ダウンロード・起動を行わない。

- `agent`: `github-copilot`
- `prompt`: この Skill より後のユーザー本文。変更しない
- `working_directory`: 今開いている Codex workspace / worktree（編集対象リポジトリ。Facade リポジトリではない）
- `session_id`: 同じ Copilot session を続けるときは、この thread の直前の `run_agent` 結果の `sessionId`
- `skills`: ユーザーが通し指定した Skill 名だけ。Codex 形式のまま渡す

作業の再計画、分割、本文の書き換えはしない。tool が返ったら `outputText` をユーザーへ見せ、次ターン用に `sessionId` を残す。
