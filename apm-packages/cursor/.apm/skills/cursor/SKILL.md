---
name: cursor
description: Codex 自身は対象作業を実行しない。Skill より後のユーザー本文を Codex Agent Facade 経由で Cursor CLI へ委譲し、その結果を中継する。この Codex thread で Cursor CLI に作業させ、Codex は薄い UI shell / relay として結果だけを返すときに使う。
user-invocable: true
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: MIT
# License: https://opensource.org/licenses/MIT
# Source: https://github.com/suusanex/codex_agent_facade
---

# Cursor CLI（Codex Agent Facade）

この Skill が指定された turn で、Codex は planner / executor / reviewer / orchestrator ではない。Codex は Cursor CLI に対する薄い UI shell / relay である。対象作業は Cursor CLI が行い、Codex はその結果を中継する。

MCP tool `start_agent` と `get_agent_job` を server `codex_agent_facade` で呼ぶ。

MCP server はこの Skill の一部ではない。ユーザーの Codex MCP 設定（publish した Facade 実行ファイルを `~/.codex/config.toml` に書く）が必要である。この Skill は server の導入・ダウンロード・起動を行わない。

## ユーザー本文の意味

この Skill より後のユーザー本文は、外部 agent に渡す作業 payload である。Codex 自身への作業実行指示として扱わない。`prompt` としてそのまま外部 agent に渡す。Codex は補足、要約、再構成、再計画、分割をしない。

## request_id と session_id の lifetime

`request_id` は 1つの論理的な `start_agent` request / agent job に対する冪等キーである。Codex thread の ID でも、外部 agent session の ID でもない。

新しい委譲 payload ごと、同じ Codex thread の新しいユーザー turn ごと、同じ外部 agent session を継続する follow-up ごと、同一 turn 内の別 agent job ごとに新しい UUID を生成する。Codex thread が同じ、または外部 agent session が同じ、という理由だけでは `request_id` を再利用しない。前回 completed job の `request_id` を次の follow-up に流用しない。

`session_id` の lifetime は異なる。同じ Cursor session をユーザー turn をまたいで続けるときは、直前の completed `result.sessionId` を再利用する。`session_id` の継続は `request_id` の再利用理由にならない。

`request_id` を再利用してよいのは Exact retry だけである。その再試行は `agent`, `prompt`, `working_directory`, `session_id`, `skills`, `auto_approve` を前回と完全一致させる。

## start_agent は毎回 full request を再構成する

`start_agent` は partial update / stateful continuation API ではない。各呼び出しは完全な RPC request である。前回の `start_agent` 引数が MCP server / Facade 側に保持されるとは仮定しない。continuation を含む毎回の呼び出しで、少なくとも次の required fields を必ず指定する。

- `request_id`
- `agent`
- `prompt`
- `working_directory`

任意 field（`session_id`, `skills`, `auto_approve`）も、その turn で必要なら明示する。前回と同じ値だからという理由で required field を省略しない。特に `working_directory` は、同じ repository / worktree を継続している場合でも毎回現在の絶対パスを解決して渡す。前回値の暗黙継承はしない。

## Codex が行ってよい処理

この Skill が指定された turn で Codex が行ってよい処理は次に限る。

1. この turn の委譲 payload 用の `request_id` を UUID で生成する。新しいユーザー turn、新しい prompt、同一 thread 内の別 agent job では新しい `request_id` を使う。同じ `request_id` を使うのは Exact retry だけである。
2. この Skill の規約に従って `start_agent` の引数を、呼び出しごとに完全な RPC として機械的に構成する。
   - `request_id`: 1つの論理的な `start_agent` request / agent job の冪等キー
   - `agent`: `cursor`
   - `prompt`: この Skill より後のユーザー本文。変更しない
   - `working_directory`: 今開いている Codex workspace / worktree の絶対パス（編集対象リポジトリ。Facade リポジトリではない）。同じ repository を継続していても毎回解決して渡す
   - `session_id`: 同じ Cursor session を続けるときは、この thread の直前の completed `result.sessionId`
   - `skills`: Cursor Driver は現在この値を明示 invoke へ変換しない。Cursor で Skill を明示実行させる場合は、ユーザー本文側に Cursor native 形式の `/skill-name` を含める
   - `auto_approve`: その turn で必要な場合だけ明示する
3. `start_agent` 直前の preflight を行う。required field が欠けている場合は呼ばない。
4. `start_agent` を呼ぶ。
5. 返された同じ `jobId` に対して `get_agent_job` を poll する。
6. terminal result を取得する。
7. `completed` なら `result.outputText` をユーザーへ中継する。これがユーザーへの主たる応答である。
8. 次の turn で同一 Cursor session を継続できるよう `result.sessionId` を保持する。この値は次の Follow-up continuation の `session_id` であり、次の `request_id` ではない。
9. Facade / tool 呼び出しそのものが失敗した場合、その失敗をユーザーへ報告する。

必要な tool invocation と job lifecycle 管理は許可する。それを超えて対象作業そのものへ Codex が参加してはならない。

## start_agent 直前の preflight

`start_agent` を呼ぶ前に、次を機械的に確認する。planner 的な判断ではなく、Facade tool を正しく呼ぶための確認である。

- `request_id` がある。この distinct payload 用の新しい UUID である。ただし Exact retry のときだけ前回と同じ値
- `agent` があり、この Skill の agent 名と一致する
- `prompt` があり、今回の委譲 payload である
- `working_directory` があり、現在の workspace / worktree の絶対パスである
- Follow-up continuation なら `session_id` は直前の completed `result.sessionId` と一致する
- Exact retry なら全ての `start_agent` 引数が前回の試行と同一である

required field が欠けている場合は `start_agent` を呼ばず、その field を補ってから呼ぶ。

## Exact retry

対象:

- `start_agent` を送った
- transport / timeout 等で結果を取得できなかった可能性がある
- 同一 job の二重起動を避けたい

動作:

- 同じ `request_id`
- 全ての `start_agent` 引数を前回と完全一致させる
- `prompt` や `session_id` 等を変更しない

これは新しいユーザー turn の follow-up ではない。前回 completed job のあとに新しい payload を送る場合は Exact retry を使わない。

## Follow-up continuation

対象:

- 前回 job が completed
- 新しいユーザー依頼 / 新しい payload を同じ Cursor session に続けて渡す

動作:

- 新しい `request_id`
- 新しい `prompt`
- `agent` を再指定する
- `working_directory` を現在の絶対パスとして再指定する。前回と同じ workspace でも省略しない
- 前回 completed result の `sessionId` を `session_id` に指定する
- その他、その turn に必要な引数を完全に再構成する

同じ Codex thread でも、同じ Cursor session でも、新しい `request_id` を使う。前回 completed job の `request_id` は使わない。

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
