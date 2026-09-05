---
name: devin-cli
description: Codex 自身は対象作業を実行しない。Skill より後のユーザー本文を Codex Agent Facade 経由で Devin CLI へ委譲し、その結果を中継する。この Codex thread で Devin CLI に作業させ、Codex は薄い UI shell / relay として結果だけを返すときに使う。
user-invocable: true
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: MIT
# License: https://opensource.org/licenses/MIT
# Source: https://github.com/suusanex/codex_agent_facade
---

# Devin CLI（Codex Agent Facade）

この Skill が指定された turn で、Codex は planner / executor / reviewer / orchestrator ではない。Codex は Devin CLI に対する薄い UI shell / relay である。対象作業は Devin CLI が行い、Codex はその結果を中継する。

MCP tool `start_agent` と `get_agent_job` を server `codex_agent_facade` で呼ぶ。

MCP server はこの Skill の一部ではない。ユーザーの Codex MCP 設定（publish した Facade 実行ファイルを `~/.codex/config.toml` に書く）が必要である。この Skill は server の導入・ダウンロード・起動を行わない。

## ユーザー本文の意味

この Skill より後のユーザー本文は、外部 agent に渡す作業 payload である。Codex 自身への作業実行指示として扱わない。`prompt` としてそのまま外部 agent に渡す。Codex は補足、要約、再構成、再計画、分割をしない。

## Codex が行ってよい処理

この Skill が指定された turn で Codex が行ってよい処理は次に限る。

1. この作業用の `request_id` を UUID で一度だけ作り、保持する。失っても同じ値を使う。新しい id で `start_agent` を打ち直さない。
2. この Skill の規約に従って `start_agent` の引数を機械的に構成する。
   - `request_id`: この作業の冪等キー。`start_agent` の結果を取り損ねたら同じ値で再試行する
   - `agent`: `devin-cli`
   - `prompt`: この Skill より後のユーザー本文。変更しない
   - `working_directory`: 今開いている Codex workspace / worktree（編集対象リポジトリ。Facade リポジトリではない）
   - `session_id`: 同じ Devin session を続けるときは、この thread の直前の completed `result.sessionId`
   - `skills`: ユーザーが通し指定した Skill 名だけ。Codex 形式のまま渡す
3. `start_agent` を呼ぶ。
4. 返された同じ `jobId` に対して `get_agent_job` を poll する。
5. terminal result を取得する。
6. `completed` なら `result.outputText` をユーザーへ中継する。これがユーザーへの主たる応答である。
7. 次の turn で同一 Devin session を継続できるよう `result.sessionId` を保持する。
8. Facade / tool 呼び出しそのものが失敗した場合、その失敗をユーザーへ報告する。

必要な tool invocation と job lifecycle 管理は許可する。それを超えて対象作業そのものへ Codex が参加してはならない。

## Codex が行ってはならない処理

委譲対象の作業について、Codex 自身は planner / executor / reviewer / orchestrator として動作しない。外部 agent へ渡した作業を目的として、Codex 自身は次を行わない。Facade tool を正しく呼び出すために必要な機械的処理はこの限りではない。

- repository / source code の調査
- Issue / PR 等の内容取得・分析
- memory の検索
- web 検索
- shell command の実行
- 独自のプラン作成
- 実装・ファイル編集
- テスト・検証
- レビュー
- 外部 agent と並行した独自調査
- 外部 agent の結果を材料にした独自の再計画・補完

特に、外部 agent を走らせながら Codex 側でも同じ Issue とコードを調査する動きは不正である。

## 外部 agent 結果の中継

`result.outputText` を受け取ったあと、Codex は独自回答を追加しない。不足していると思った箇所を補完しない。別の調査結果を混ぜない。外部 agent の結論を再設計しない。自分で続きを実装しない。この Skill の役割は relay である。外部 agent の結果そのものがユーザーへの主たる応答である。
