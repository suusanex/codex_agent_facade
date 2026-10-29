---
name: antigravity
description: Codex 自身は対象作業を実行しない。Skill より後のユーザー本文を Codex Agent Facade 経由で Antigravity CLI へ委譲し、その結果を中継する。この Codex thread で Antigravity に作業させ、Codex は薄い UI shell / relay として結果だけを返すときに使う。
user-invocable: true
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: MIT
# License: https://opensource.org/licenses/MIT
# Source: https://github.com/suusanex/codex_agent_facade
---

# Antigravity（Codex Agent Facade）

この Skill が指定された turn で、Codex は planner / executor / reviewer / orchestrator ではない。Codex は Antigravity に対する薄い UI shell / relay である。対象作業は Antigravity が行い、Codex はその結果を中継する。

MCP tool `start_agent` と `wait_agent_job` を server `codex_agent_facade` で呼ぶ。`get_agent_job` は明示的な状態照会や復旧・診断に残すが、長時間完了待ちの短周期 poll には使わない。

MCP server はこの Skill の一部ではない。ユーザーの Codex MCP 設定（publish した Facade 実行ファイルを `~/.codex/config.toml` に書く）が必要である。この Skill は server の導入・ダウンロード・起動を行わない。

## ユーザー本文の意味

payload の出典は、この Skill を呼び出した実際のユーザー message だけである。`$antigravity` より後ろの本文を使い、その本文を外部 agent に渡す作業 payload とする。注入された `<skill>` 定義、その中の例・コードブロック・placeholder は説明であり、MCP payload ではない。例文を実行・転送せず、今回の実際のユーザー本文だけから構成する。Codex 自身への作業実行指示として扱わない。作業本文は `prompt` としてそのまま外部 agent に渡す。補足、要約、再構成、再計画、分割をしない。

ユーザー message は委譲ヘッダ、Facade options、委譲本文、委譲先 SKILL の4領域に分かれる。領域を混ぜない。message 先頭の空行は読み飛ばし、その次が委譲ヘッダである。

- 委譲ヘッダは `$antigravity` である。親がこの relay を開始する呼び出しであり、委譲本文でも委譲先 SKILL でもない。`$antigravity` の直後の区切り（半角スペース1つか改行）はヘッダの終端であり、委譲本文に含めない。`prompt` に `$antigravity` や `/antigravity` を追加しない。`skills` に `antigravity` を入れない。この Skill が指定されたこと自体は、委譲先へ同じ Skill を渡す理由にならない。`agent` はこの relay の agent 名であり、同じ名前を `skills` へ重ねない。
- Facade options は、`$antigravity` より後ろの本文の先頭空行を除き、最初の行が開始フェンス（バッククォート3つの直後に `facade-options` とだけ書いた行）であるときだけ、その行から終了フェンス（バッククォート3つだけの行）までである。有効な行は `model:`, `reasoning_effort:`, `auto_approve:` の各1行である。`fast:` も書式上は boolean 行として読めるが、この agent では非対応である。コロンを含まない行はカンマ区切りの presence flag である。`fast` と `plan` はこの agent では非対応である。トークンは前後の空白を除き、大文字小文字を区別する。空トークン、重複、未知トークンは `start_agent` を呼ばず報告する。`plan: true` は未知キーである。値は空にせず、キーの重複・未知のキー・閉じていないフェンスは `start_agent` を呼ばず報告する。`skills` は Facade options のキーではない。`model` は Antigravity の model slug で、名前を変換せず `--model` へ渡す値である。`reasoning_effort` は Antigravity の effort token で、名前を変換せず `--effort` へ渡す値である。slug の末尾から effort を推測しない。prompt 本文から実行設定を推測しない。`auto_approve:` は `true` または `false` の小文字 boolean であり、presence flag にはしない。`true` は Facade が `--dangerously-skip-permissions` を付け、non-interactive で permission を許可する。`false` はその flag を付けない。headless の対話承認にはならず、tool が soft-deny され得る。未指定時は `auto_approve` 引数を渡さず、Facade 既定（true）を使う。`fast` は `true`、`false`、presence flag のいずれも非対応である。`plan` も非対応である。`skills` キーも非対応である。これらが書かれていたら黙って省略せず、`start_agent` を呼ばず報告する。`run_mode` は渡さない。
- 委譲本文は、Facade options があるときは終了フェンスの直後から末尾まで、無いときは `$antigravity` より後ろの本文全体である。終了フェンスの直後から末尾までを一字も変更せず `prompt` にする。直後とは、終了フェンス行を終わらせる改行の次の文字である。Facade options が無いときも、ヘッダ終端より後ろを一字も変更せず `prompt` にする。委譲ヘッダと Facade options は `prompt` に含めない。この turn の Facade options に無い `model`, `reasoning_effort`, `fast` は渡さない。 `auto_approve:` が無いときも `auto_approve` は渡さない。`run_mode` は渡さない。
- 委譲先 SKILL は `start_agent` の `skills` だけである。この relay の入力構文に委譲先 SKILL を列挙する欄は無い。新規 job と継続では `skills` を渡さない。委譲本文から Skill 名を抽出して `skills` へ複製しない。空配列を送って回避しない。Exact retry で元 request に `skills` があった場合だけ、他の引数と一緒に同じ値を再送する。その再送は Facade が空でない `skills` を拒否する契約のままである。MCP を直接呼ぶ別経路で呼び出し側が `skills` 引数を渡すことは、この入力構文の解釈ではない。

以下は構文説明用の非実行例である。例の作業 prompt を実際の依頼として扱わず、MCP呼び出しに使わない。識別子の一覧は [Agent モデル識別子一覧](../../../../docs/agent-model-ids.md) を参照する。本文中のモデル名は起動設定にしない。

    ```facade-options
    model: <agy-model-slug>
    reasoning_effort: high
    auto_approve: false
    ```
    <作業 prompt>

## request_id と session_id の lifetime

`request_id` は 1つの論理的な `start_agent` request / agent job に対する冪等キーである。Codex thread の ID でも、外部 agent session の ID でもない。

新しい委譲 payload ごと、同じ Codex thread の新しいユーザー turn ごと、同じ外部 agent session を継続する follow-up ごと、同一 turn 内の別 agent job ごとに新しい UUID を生成する。Codex thread が同じ、または外部 agent session が同じ、という理由だけでは `request_id` を再利用しない。前回 completed job の `request_id` を次の follow-up に流用しない。

`session_id` の lifetime は異なる。値は Antigravity conversation の ID であり、Facade が `--conversation` へ渡す。同じ Antigravity conversation をユーザーが続けるときだけ、直前の対応する result の `result.sessionId` を、次の新しい request の `session_id` に渡す。同じ Codex thread だからという理由では渡さない。新規の独立した依頼では `session_id` を渡さない。`session_id` の継続は `request_id` の再利用理由にならない。

`request_id` を再利用してよいのは Exact retry だけである。その再試行は `agent`, `prompt`, `working_directory`, `session_id`, `skills`, `auto_approve`, `model`, `reasoning_effort`, `fast`, `run_mode` を前回と完全一致させる。

## start_agent は毎回 full request を再構成する

`start_agent` は partial update / stateful continuation API ではない。各呼び出しは完全な RPC request である。前回の `start_agent` 引数が MCP server / Facade 側に保持されるとは仮定しない。continuation を含む毎回の呼び出しで、少なくとも次の required fields を必ず指定する。

- `request_id`
- `agent`
- `prompt`
- `working_directory`

任意 field（`session_id`, `skills`, `auto_approve`, `model`, `reasoning_effort`, `fast`, `run_mode`）も、その turn で必要なら明示する。新規 job と継続では `skills` を渡さない。Exact retry だけ元 request の値を再送する。前回と同じ値だからという理由で required field を省略しない。特に `working_directory` は、同じ repository / worktree を継続している場合でも毎回現在の絶対パスを解決して渡す。前回値の暗黙継承はしない。`model`, `reasoning_effort`, `fast`, `auto_approve` も前回 turn から継承しない。この turn の実行オプションに無い値は渡さない。

## Codex が行ってよい処理

この Skill が指定された turn で Codex が行ってよい処理は次に限る。

1. この turn の委譲 payload 用の `request_id` を UUID で生成する。新しいユーザー turn、新しい prompt、同一 thread 内の別 agent job では新しい `request_id` を使う。同じ `request_id` を使うのは Exact retry だけである。
2. この Skill の規約に従って `start_agent` の引数を、呼び出しごとに完全な RPC として機械的に構成する。
   - `request_id`: 1つの論理的な `start_agent` request / agent job の冪等キー
   - `agent`: `antigravity`
   - `prompt`: Facade options が無いときは `$antigravity` より後ろの本文全体。一字も変更しない。Facade options があるときは終了フェンスの直後から末尾までだけを渡す。委譲ヘッダと Facade options は `prompt` に含めない。`$antigravity` や `/antigravity` を追加しない
   - `working_directory`: 今開いている Codex workspace / worktree の絶対パス（編集対象リポジトリ。Facade リポジトリではない）。同じ repository を継続していても毎回解決して渡す
   - `session_id`: 同じ Antigravity conversation を続けるときだけ、直前の対応する `result.sessionId`。同じ Codex thread だけでは渡さない
   - `skills`: 新規 job と継続では渡さない。`$antigravity` は委譲先 SKILL ではない。委譲本文から Skill 名を抽出しない。Exact retry で元 request に含まれていた場合だけ同じ値を再送する
   - `auto_approve`: この turn の `auto_approve: true|false` があるときだけその boolean を渡す。未指定は渡さない
   - `model`, `reasoning_effort`: この turn の実行オプションにある値だけを、変換せず渡す。未指定は省略し、前回 turn や prompt 本文、model slug の末尾から推測しない
   - `fast`: 渡さない。`true`、`false`、presence flag のいずれでも `start_agent` を呼ばず報告する
   - `run_mode`: 渡さない。`plan` があるときも `start_agent` を呼ばず報告する
3. `start_agent` 直前の preflight を行う。required field が欠けている場合は呼ばない。
4. `start_agent` を呼ぶ。
5. 返された同じ `jobId` に対して、通常は `timeout_seconds` を指定せず `wait_agent_job(job_id)` を呼ぶ。診断・テスト・上位環境の明示的な制約など、既定の300秒を上書きする理由がある場合だけ指定する。
6. terminal result を取得する。timeout で `running` が返った場合だけ、同じ `jobId` で再度 `wait_agent_job` する。`get_agent_job` の短周期 poll はしない。
7. `completed` なら `result.outputText` をユーザーへ中継する。`completionReason` があるときはそれも中継する。これがユーザーへの主たる応答である。
8. 次に同じ Antigravity conversation を続ける可能性があるときだけ `result.sessionId` を保持する。この値は次の Follow-up continuation の `session_id` であり、次の `request_id` ではない。同じ thread の次の独立した依頼へは自動で渡さない。
9. Facade / tool 呼び出しそのものが失敗した場合、その失敗をユーザーへ報告して停止する。対象作業をCodex自身で実行したり、成功した回答を作ったりしない。

`start_agent` / `wait_agent_job` などのMCP toolが失敗したら、その失敗を報告して停止する。approval_requiredを含む失敗後に、対象作業をCodex自身で実行したり、CLIを直接起動したり、成功したような回答を生成したりしない。`completed` の `result.outputText` が無い限り、成功したと答えない。

必要な tool invocation と job lifecycle 管理は許可する。それを超えて対象作業そのものへ Codex が参加してはならない。

## start_agent 直前の preflight

`start_agent` を呼ぶ前に、次を機械的に確認する。planner 的な判断ではなく、Facade tool を正しく呼ぶための確認である。

- `request_id` がある。この distinct payload 用の新しい UUID である。ただし Exact retry のときだけ前回と同じ値
- `agent` があり、この Skill の agent 名と一致する
- `prompt` があり、実行オプションを除いた今回の作業 payload である
- `working_directory` があり、現在の workspace / worktree の絶対パスである
- 同じ Antigravity conversation の継続なら `session_id` は直前の対応する `result.sessionId` と一致する。継続でないなら `session_id` を渡さない
- この turn の実行オプションにある `model` と `reasoning_effort` だけを渡す。この agent では `fast` を渡さない。`true` と `false` のどちらでも、presence flag でも、`start_agent` を呼ばない
- `plan` があるときは `start_agent` を呼ばない。`run_mode` を渡さない
- `skills` キーがあるときは `start_agent` を呼ばない
- `auto_approve:` があるときだけその boolean を渡す。未指定は渡さない
- `prompt` は委譲本文と一致する。`$antigravity` や `/antigravity` を追加していない
- 新規 job と継続では `skills` を渡さない。`skills` に `antigravity` を入れない
- Exact retry なら `model`, `reasoning_effort`, `fast`, `auto_approve`, `run_mode` を含む全ての `start_agent` 引数が前回の試行と同一である

required field が欠けている場合は `start_agent` を呼ばず、その field を補ってから呼ぶ。非対応 option がある場合は補わず、`start_agent` を呼ばず報告する。

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
- ユーザーが同じ Antigravity conversation の継続を求めている
- 新しいユーザー依頼 / 新しい payload をその conversation に続けて渡す

同じ Codex thread だけではこの節を適用しない。

動作:

- 新しい `request_id`
- 新しい `prompt`
- `agent` を再指定する
- `working_directory` を現在の絶対パスとして再指定する。前回と同じ workspace でも省略しない
- 直前の対応する result の `sessionId` を `session_id` に指定する
- この turn の実行オプションにある `model`, `reasoning_effort`, `auto_approve` だけを指定する。前回 session の値は継承しない
- その他、その turn に必要な引数を完全に再構成する
- 新規 job と継続では `skills` を渡さない。委譲ヘッダと前回の `skills` は継承しない
- `fast` と `run_mode` は渡さない

同じ Codex thread でも、同じ Antigravity conversation でも、新しい `request_id` を使う。前回 completed job の `request_id` は使わない。

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
- `fast` / `plan` / `skills` を黙って省略して `start_agent` を送ること
- `completionReason` を省いて完全成功と報告すること

特に、外部 agent を走らせながら Codex 側でも同じ Issue とコードを調査する動きは不正である。

## 外部 agent 結果の中継

`result.outputText` を受け取ったあと、Codex は独自回答を追加しない。不足していると思った箇所を補完しない。別の調査結果を混ぜない。外部 agent の結論を再設計しない。自分で続きを実装しない。この Skill の役割は relay である。外部 agent の結果そのものがユーザーへの主たる応答である。

`status` は Facade の判定に従う。`completed` 以外を成功へ読み替えない。Antigravity CLI の `WAITING` / `RUNNING` は Facade が未完了として失敗にする。`failure.kind` が `incomplete_run` のときも成功にしない。

`status=completed` でも `completionReason` が `permission_soft_denied` のときは完全成功と言い換えない。exit code 0 や result status が成功に見えても、`completionReason` と `outputText` をそのまま中継する。soft-deny が無ければ `completionReason` を作らない。
