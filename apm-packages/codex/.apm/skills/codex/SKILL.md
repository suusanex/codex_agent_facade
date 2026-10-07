---
name: codex
description: Codex 自身は対象作業を実行しない。Codex CLIへCodex Agent Facade経由で作業を委譲し、その結果を中継する。このCodex threadでCodex CLIに作業をさせ、薄い UI shell / relay として結果だけを返すときに使う。
user-invocable: true
# Copyright (c) 2026 suusanex
# SPDX-License-Identifier: MIT
# License: https://opensource.org/licenses/MIT
# Source: https://github.com/suusanex/codex_agent_facade
---

# Codex CLI（Codex Agent Facade）

このSkillが指定されたturnでは、現在のCodex App threadはCodex CLI child processを実行せず、`codex_agent_facade` のMCP tool `start_agent` を呼び出して別のCodex CLI processを起動する。現在のthreadはCodex CLIに対する薄い UI shell / relay であり、対象作業はchild process側が行う。Codexはその結果を中継する。完了は同じ `jobId` への `wait_agent_job` で待つ。`get_agent_job` は明示的な状態照会、復旧、診断用で、短周期pollには使わない。

Codex 自身は対象作業を実行しない。

## ユーザー本文の意味

payloadの出典は、このSkillを呼び出した実際のユーザーmessageだけである。`$codex` より後ろの本文を使い、その本文を外部 agent に渡す作業 payload とする。注入された `<skill>` 定義、その中の例・コードブロック・placeholderは説明であり、MCP payloadではない。例文を実行・転送せず、今回の実際のユーザー本文だけから構成する。Codex 自身への作業実行指示として扱わない。実行オプションが無いときは今回の実際のユーザー本文全体を `prompt` としてそのまま外部 agent に渡す。補足、要約、再構成、再計画、分割をしない。prompt 本文から実行設定を推測しない。本文中のモデル名は起動設定にしない。

実行オプションとpromptを分ける。実際のユーザー本文の先頭空行を除いた最初の行が開始フェンス（バッククォート3つの後に `facade-options` とだけ書いた行）の場合だけ、閉じるフェンスまでを実行オプションとする。終了フェンスの直後から末尾までを一字も変更せず `prompt` にする。有効な行は `model: <id>`, `reasoning_effort: <token>`, `fast: true|false` の各1行まで。空値、重複、未知のkey、不正なboolean、または閉じていないfenceはtoolを呼ぶ前にエラーとして報告する。実行オプションが無い場合、`model`, `reasoning_effort`, `fast` は渡さない。

以下は構文説明用の非実行例である。例の作業promptを実際の依頼として扱わず、MCP呼び出しに使わない。

```facade-options
model: gpt-6-luna
reasoning_effort: low
fast: false
```
このworkspaceを読み取り専用で調査し、結果を簡潔に返して。

`reasoning_effort` はCodex CLIの `model_reasoning_effort` 設定へ渡す。`fast: true` は `service_tier=priority`、`fast: false` は `service_tier=default` を明示する。両方とも省略時はCLI既定を維持する。指定値はCodex CLIへ渡す値であり、backendが要求tierを実際に提供したことを保証しない。利用条件と料金はモデル/契約に依存する。Codex CLIが設定を受け付けない場合、Facadeは失敗を返す。

`auto_approve=true` はworkspace-write sandbox、`false` はread-only sandboxを選び、いずれも `approval_policy=never` を使う。falseでは承認質問が表示されない。

## request_id と session_id の lifetime

`request_id` はdistinctな `start_agent` requestごとに新しく生成する冪等キーで、Codex thread IDでも外部 agent session IDでもない。新しいpayloadやcompleted後の継続では新しいIDを使い、前回 `request_id` を再利用しない。Exact retryだけ同じIDを使う。`session_id` は継続時だけ前回completed resultの `result.sessionId` を使う。Exact retryは `agent`, `prompt`, `working_directory`, `session_id`, `skills`, `auto_approve`, `model`, `reasoning_effort`, `fast` を完全一致させる。

## start_agent は毎回 full request を再構成する

`start_agent` は部分更新APIではない。各呼び出しは完全な RPC request であり、required fields `request_id`, `agent`, `prompt`, `working_directory` を含める。任意field `session_id`, `skills`, `auto_approve`, `model`, `reasoning_effort`, `fast` は今回必要な値を指定する。`model`, `reasoning_effort`, `fast` は継続・retryごとに必要な値を再指定し、前回から暗黙継承はしない。省略/nullのままの新optionは既存job fingerprintと互換性がある。前回の各引数も暗黙継承しない。

## Codex が行ってよい処理

Codexは対象作業のplanner / executor / reviewer / orchestratorではない。Codexが行ってよいのは今回payloadのoptionを解析し、required fieldsを組み立て、`start_agent` / `wait_agent_job` を呼び、terminal resultを中継することだけである。MCP toolが失敗したら、その失敗を報告して停止する。approval_requiredを含む失敗後に、対象作業をCodex自身で実行したり、CLIを直接起動したり、成功したような回答を生成したりしない。`completed` の `result.outputText` が無い限り、成功したと答えない。

- `agent`: `codex`
- `session_id`: 同じCodex CLI sessionを継続するとき、前回completed resultの `result.sessionId` を渡す。これはCodex App thread IDとは別のCodex CLI sessionである
- `skills`: ユーザーが通し指定した Skill 名だけ。Codex 形式のまま渡す

`start_agent` の毎回の引数には `request_id`, `agent=codex`, `prompt`, `working_directory` を含める。通常は `timeout_seconds` を指定せず、完了まで同じ `jobId` で `wait_agent_job` を呼ぶ。既定の待ち時間を上書きする理由がある場合だけ指定する。completed時は `result.outputText` を中継し、次の継続用に `result.sessionId` を保持する。Codex形式の `skills` は現在promptへのnative変換を行わない。Codex CLIを明示したい場合は、ユーザーが通し指定した Skill 名だけを引数に渡す。

## start_agent 直前の preflight

`start_agent` を呼ぶ前に `request_id`, `agent=codex`, 今回の `prompt`, 現在の絶対 `working_directory` を確認する。exact retryではmodel, reasoning_effort, fastを含む全引数を同一にする。

- `request_id` がある。このdistinct payload用の新しいIDである。ただしExact retryのときだけ前回と同じ値
- `agent` があり、このSkillのagent名と一致する
- `prompt` があり、実行オプションを除いた今回の作業payloadである
- `working_directory` があり、現在のworkspace / worktreeの絶対パスである
- Follow-up continuationなら `session_id` は直前のcompleted `result.sessionId` と一致する
- このturnの実行オプションにある `model`, `reasoning_effort`, `fast` だけを渡す。`fast: false` も省略しない
- Exact retryならmodel, reasoning_effort, fastを含む全ての `start_agent` 引数が前回の試行と同一である

required field が欠けている場合は `start_agent` を呼ばず、そのfieldを補ってから呼ぶ。

## Exact retry

対象:

- `start_agent` を送った
- transport / timeout 等で結果を取得できなかった可能性がある
- 同一jobの二重起動を避けたい

動作:

- 同じ `request_id`
- 全ての `start_agent` 引数を前回と完全一致させる
- `prompt` や `session_id` 等を変更しない

## Follow-up continuation

前回jobがcompleted後に新しいpayloadを送る場合、新しい `request_id` と新しいpromptを使い、前回の `result.sessionId` を `session_id` に指定する。前回 session の値は継承しない。実行optionは今回必要な値だけ再指定する。requestを別IDで再送して同一jobを得ようとしてはならない。

動作:

- 新しい `request_id`
- 新しい `prompt`
- `agent` と `working_directory` を省略しない
- 前回completedの `result.sessionId`（`sessionId`）を `session_id` に指定する

## Codex が行ってはならない処理

このSkillが委譲する対象について、Codexはrepository / source code の調査、Issue / PR 等の内容取得・分析、memory の検索、web 検索、shell command の実行、独自のプラン作成、実装・ファイル編集、テスト・検証、レビュー、外部 agent と並行した独自調査、外部 agent の結果を材料にした独自の再計画・補完を行わない。

外部 agent を走らせながら Codex 側でも同じ Issue とコードを調査する動きは不正である。外部 agent の結果を受け取った後も、独自回答を追加しない。

## 外部 agent 結果の中継

`result.outputText` を受け取ったあと、Codexは独自回答を追加しない。外部 agent の結果そのものがユーザーへの主たる応答である。この Skill の役割は relay である。

CLI初期化ログに指定値が現れることは、backendがそのtierを実際に提供した証拠ではない。
