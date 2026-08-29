---
name: devin-cli
description: Codex 上のユーザー本文を Codex Agent Facade の MCP tool start_agent / get_agent_job 経由で Devin CLI へ渡す。この Codex thread で Devin CLI に実装させたいときに使う。
user-invocable: true
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: MIT
# License: https://opensource.org/licenses/MIT
# Source: https://github.com/suusanex/codex_agent_facade
---

# Devin CLI（Codex Agent Facade）

MCP tool `start_agent` と `get_agent_job` を server `codex_agent_facade` で呼ぶ。

MCP server はこの Skill の一部ではない。ユーザーの Codex MCP 設定（publish した Facade 実行ファイルを `~/.codex/config.toml` に書く）が必要である。この Skill は server の導入・ダウンロード・起動を行わない。

このユーザー作業に対して `request_id` を UUID で一度だけ作り、失っても同じ値を使う。新しい id で `start_agent` を打ち直さない。

- `request_id`: この作業の冪等キー。`start_agent` の結果を取り損ねたら同じ値で再試行する
- `agent`: `devin-cli`
- `prompt`: この Skill より後のユーザー本文。変更しない
- `working_directory`: 今開いている Codex workspace / worktree（編集対象リポジトリ。Facade リポジトリではない）
- `session_id`: 同じ Devin session を続けるときは、この thread の直前の completed `result.sessionId`
- `skills`: ユーザーが通し指定した Skill 名だけ。Codex 形式のまま渡す

作業の再計画、分割、本文の書き換えはしない。`start_agent` が返した `jobId` で `get_agent_job` を poll し、`completed` なら `result.outputText` をユーザーへ見せ、次ターン用に `result.sessionId` を残す。
