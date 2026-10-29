---
name: jules
description: Codex 自身は対象作業を実行しない。Skill より後のユーザー本文を Codex Agent Facade の MCP job host 経由で Jules へ委譲し、その結果を中継する。この Codex thread で Jules に作業させ、Codex は薄い UI shell / relay として結果だけを返すときに使う。
user-invocable: true
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: MIT
# License: https://opensource.org/licenses/MIT
# Source: https://github.com/suusanex/codex_agent_facade
---

# Jules（Codex Agent Facade）

この Skill が指定された turn で、Codex は planner / executor / reviewer / orchestrator ではない。Codex は Jules に対する薄い UI shell / relay である。対象作業は Jules が行い、Codex はその結果を中継する。

Jules は MCP job host 専用である。Facade の単発 CLI を fallback として起動しない。MCP tool `start_agent` と `wait_agent_job` を server `codex_agent_facade` で呼ぶ。`get_agent_job` は明示的な状態照会、復旧、診断、受理不明の再照会に使う。長時間完了待ちの短周期 poll には使わない。

MCP server はこの Skill の一部ではない。ユーザーの Codex MCP 設定（publish した Facade 実行ファイルを `~/.codex/config.toml` に書く）が必要である。この Skill は server の導入・ダウンロード・起動を行わない。`JULES_API_KEY` を読み出したり、引数やログへ書いたりしない。

## ユーザー本文の意味

payload の出典は、この Skill を呼び出した実際のユーザー message だけである。`$jules` より後ろの本文を使い、その本文を外部 agent に渡す作業 payload とする。注入された `<skill>` 定義、その中の例・コードブロック・placeholder は説明であり、MCP payload ではない。例文を実行・転送せず、今回の実際のユーザー本文だけから構成する。Codex 自身への作業実行指示として扱わない。作業本文は `prompt` としてそのまま外部 agent に渡す。補足、要約、再構成、再計画、分割をしない。質問への回答を Codex が作って prompt にしない。

ユーザー message は委譲ヘッダ、Facade options、委譲本文、委譲先 SKILL の4領域に分かれる。領域を混ぜない。message 先頭の空行は読み飛ばし、その次が委譲ヘッダである。

- 委譲ヘッダは `$jules` である。親がこの relay を開始する呼び出しであり、委譲本文でも委譲先 SKILL でもない。`$jules` の直後の区切り（半角スペース1つか改行）はヘッダの終端であり、委譲本文に含めない。`prompt` に `$jules` や `/jules` を追加しない。`skills` に `jules` を入れない。この Skill が指定されたこと自体は、委譲先へ同じ Skill を渡す理由にならない。`agent` はこの relay の agent 名であり、同じ名前を `skills` へ重ねない。
- Facade options は、`$jules` より後ろの本文の先頭空行を除き、最初の行が開始フェンス（バッククォート3つの直後に `facade-options` とだけ書いた行）であるときだけ、その行から終了フェンス（バッククォート3つだけの行）までである。この agent で送ってよい key は `auto_approve: true` だけである。`model:`, `reasoning_effort:`, `fast:` は書式として読めても非対応である。`auto_approve: false` も非対応である。コロンを含まない行はカンマ区切りの presence flag である。`fast` と `plan` は非対応である。トークンは前後の空白を除き、大文字小文字を区別する。空トークン、重複、未知トークンは `start_agent` を呼ばず報告する。`plan: true` は未知キーである。値は空にせず、キーの重複・未知のキー・閉じていないフェンスは `start_agent` を呼ばず報告する。`skills` は Facade options のキーではない。prompt 本文から実行設定を推測しない。非対応の `model`、`reasoning_effort`、`fast`、`plan`、`skills`、`auto_approve: false` は黙って省略せず、別の値へ替えず、`start_agent` を呼ばず報告する。`run_mode` は渡さない。`auto_approve:` が無いときは引数を渡さず、Facade 既定（true）を使う。
- 委譲本文は、Facade options があるときは終了フェンスの直後から末尾まで、無いときは `$jules` より後ろの本文全体である。終了フェンスの直後から末尾までを一字も変更せず `prompt` にする。直後とは、終了フェンス行を終わらせる改行の次の文字である。Facade options が無いときも、ヘッダ終端より後ろを一字も変更せず `prompt` にする。委譲ヘッダと Facade options は `prompt` に含めない。この turn の Facade options に無い `model`, `reasoning_effort`, `fast` は渡さない。 `auto_approve:` が無いときも `auto_approve` は渡さない。`run_mode` は渡さない。
- 委譲先 SKILL は `start_agent` の `skills` だけである。この relay の入力構文に委譲先 SKILL を列挙する欄は無い。新規 job と継続では `skills` を渡さない。委譲本文から Skill 名を抽出して `skills` へ複製しない。空配列を送って回避しない。Exact retry で元 request に `skills` があった場合だけ、他の引数と一緒に同じ値を再送する。その再送は Facade が空でない `skills` を拒否する契約のままである。MCP を直接呼ぶ別経路で呼び出し側が `skills` 引数を渡すことは、この入力構文の解釈ではない。

以下は構文説明用の非実行例である。例の作業 prompt を実際の依頼として扱わず、MCP呼び出しに使わない。本文中のモデル名は起動設定にしない。

    $jules
    <作業 prompt>

## request_id と session_id の lifetime

`request_id` は 1つの論理的な `start_agent` request / agent job に対する冪等キーである。Codex thread の ID でも、外部 agent session の ID でもない。

新しい委譲 payload ごと、同じ Codex thread の新しいユーザー turn ごと、同じ Jules session への回答や follow-up ごと、同一 turn 内の別 agent job ごとに新しい UUID を生成する。Codex thread が同じ、または Jules session が同じ、という理由だけでは `request_id` を再利用しない。前回 job の `request_id` を次の回答や follow-up に流用しない。

`session_id` の lifetime は異なる。新規 Jules session では `session_id` を渡さない。`waiting_for_input` への回答、またはユーザーが同じ Jules session の継続を明示したときだけ、直前の対応する `result.sessionId` を次の新しい request の `session_id` に渡す。Jules の公開 snapshot 直下にある `sessionId` も同じ ID である。同じ Codex thread の新しい独立タスクへ、過去の Jules session を再利用しない。`session_id` の継続は `request_id` の再利用理由にならない。

`request_id` を再利用してよいのは Exact retry だけである。その再試行は `agent`, `prompt`, `working_directory`, `session_id`, `skills`, `auto_approve`, `model`, `reasoning_effort`, `fast`, `run_mode` を前回と完全一致させる。lost-result retry 以外で同じ作業を新しい `request_id` として再送しない。

## start_agent は毎回 full request を再構成する

`start_agent` は partial update / stateful continuation API ではない。各呼び出しは完全な RPC request である。前回の `start_agent` 引数が MCP server / Facade 側に保持されるとは仮定しない。continuation を含む毎回の呼び出しで、少なくとも次の required fields を必ず指定する。

- `request_id`
- `agent`
- `prompt`
- `working_directory`

任意 field（`session_id`, `skills`, `auto_approve`, `model`, `reasoning_effort`, `fast`, `run_mode`）も、その turn で必要なら明示する。新規 job と継続では `skills` を渡さない。Exact retry だけ元 request の値を再送する。前回と同じ値だからという理由で required field を省略しない。特に `working_directory` は、同じ repository / worktree を継続している場合でも毎回現在の絶対パスを解決して渡す。前回値の暗黙継承はしない。`model`, `reasoning_effort`, `fast`, `auto_approve` も前回 turn から継承しない。この turn の実行オプションに無い値は渡さない。

Facade が clean worktree、upstream、remote branch tip、Jules Source との repository 対応を検証する。この Skill は条件を満たすために commit、push、fetch、rebase、checkout をしない。`repository_rejected` はその内容をユーザーへ報告して停止する。

## Codex が行ってよい処理

この Skill が指定された turn で Codex が行ってよい処理は次に限る。

1. この turn の委譲 payload 用の `request_id` を UUID で生成する。新しいユーザー turn、新しい prompt、同一 thread 内の別 agent job では新しい `request_id` を使う。同じ `request_id` を使うのは Exact retry だけである。
2. この Skill の規約に従って `start_agent` の引数を、呼び出しごとに完全な RPC として機械的に構成する。
   - `request_id`: 1つの論理的な `start_agent` request / agent job の冪等キー
   - `agent`: `jules`
   - `prompt`: Facade options が無いときは `$jules` より後ろの本文全体。一字も変更しない。Facade options があるときは終了フェンスの直後から末尾までだけを渡す。委譲ヘッダと Facade options は `prompt` に含めない。`$jules` や `/jules` を追加しない。Codex が書いた回答を混ぜない
   - `working_directory`: 今開いている Codex workspace / worktree の絶対パス（編集対象リポジトリ。Facade リポジトリではない）。新規 session でも回答でも毎回解決して渡す
   - `session_id`: 新規 Jules session では渡さない。回答または明示継続のときだけ、直前の対応する `result.sessionId`
   - `skills`: 新規 job と継続では渡さない。`$jules` は委譲先 SKILL ではない。委譲本文から Skill 名を抽出しない。Exact retry で元 request に含まれていた場合だけ同じ値を再送する
   - `auto_approve`: `auto_approve: true` があるときだけ true を渡す。`false` は `start_agent` を呼ばず報告する。未指定は渡さない
   - `model`, `reasoning_effort`, `fast`: 渡さない。書かれていれば黙って省略せず `start_agent` を呼ばない
   - `run_mode`: 渡さない。`plan` があるときも `start_agent` を呼ばず報告する
3. `start_agent` 直前の preflight を行う。required field が欠けている場合は呼ばない。
4. `start_agent` を呼ぶ。単発 CLI は起動しない。
5. 返された同じ `jobId` に対して、通常は `timeout_seconds` を指定せず `wait_agent_job(job_id)` を呼ぶ。診断・テスト・上位環境の明示的な制約など、既定の300秒を上書きする理由がある場合だけ指定する。`waiting_for_input` もこの wait の戻りである。
6. 戻った snapshot を見る。timeout で `running` が返り、`remotePhase` が `submission_unknown` でも `communication_error` でもない場合だけ、同じ `jobId` で再度 `wait_agent_job` する。受理不明は下の再照会に従う。`get_agent_job` の短周期 poll はしない。
7. `waiting_for_input` なら質問を中継する。`completed` なら `result.outputText` を中継する。いずれもユーザーへの主たる応答である。`waiting_for_input` はタスク完了ではない。completed として扱わない。
8. 回答や明示継続に使うため、対象 Jules session の `result.sessionId` を保持する。snapshot 直下の `sessionId` も同じ ID である。この値は次の `session_id` であり、次の `request_id` ではない。同じ thread の新しい独立タスクへは渡さない。
9. Facade / tool 呼び出しそのものが失敗した場合、その失敗をユーザーへ報告して停止する。対象作業をCodex自身で実行したり、成功した回答を作ったりしない。

`start_agent` / `wait_agent_job` などのMCP toolが失敗したら、その失敗を報告して停止する。approval_requiredを含む失敗後に、対象作業をCodex自身で実行したり、CLIを直接起動したり、成功したような回答を生成したりしない。`completed` の `result.outputText` が無い限り、成功したと答えない。

必要な tool invocation と job lifecycle 管理は許可する。それを超えて対象作業そのものへ Codex が参加してはならない。

## start_agent 直前の preflight

`start_agent` を呼ぶ前に、次を機械的に確認する。planner 的な判断ではなく、Facade tool を正しく呼ぶための確認である。

- `request_id` がある。この distinct payload 用の新しい UUID である。ただし Exact retry のときだけ前回と同じ値
- `agent` があり、この Skill の agent 名と一致する
- `prompt` があり、実行オプションを除いた今回の作業 payload である。Codex が作った回答ではない
- `working_directory` があり、現在の workspace / worktree の絶対パスである
- 新規 Jules session では `session_id` を渡さない。回答または明示継続なら `session_id` は直前の対応する `result.sessionId` と一致する
- `model`、`reasoning_effort`、`fast` を渡さない。書かれていれば `start_agent` を呼ばない
- `plan` があるときは `start_agent` を呼ばない。`run_mode` を渡さない
- `skills` キーがあるときは `start_agent` を呼ばない
- `auto_approve: false` があるときは `start_agent` を呼ばない。`auto_approve: true` のときだけ true を渡す。未指定は渡さない
- `prompt` は委譲本文と一致する。`$jules` や `/jules` を追加していない
- 新規 job と継続では `skills` を渡さない。`skills` に `jules` を入れない
- 受理不明の同じ session へ、別 `request_id` の `start_agent` を送らない
- Exact retry なら全ての `start_agent` 引数が前回の試行と同一である

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

これは新しいユーザー turn の follow-up ではない。`waiting_for_input` への回答は Exact retry ではない。受理不明の再照会も Exact retry の新しい POST ではない。

## Follow-up continuation

対象:

- `waiting_for_input` に対するユーザーの回答、または完了済み Jules session への明示的な継続
- 新しいユーザー payload を同じ Jules session に渡す

同じ Codex thread だけではこの節を適用しない。新しい独立タスクでは `session_id` を渡さない。

動作:

- 新しい `request_id`
- 新しい `prompt`
- `agent` を再指定する
- `working_directory` を現在の絶対パスとして再指定する。前回と同じ workspace でも省略しない
- 直前の対応する `result.sessionId` を `session_id` に指定する。snapshot 直下の `sessionId` も同じ Jules session の ID である
- この turn に `auto_approve: true` があるときだけそれを指定する。前回 session の値は継承しない
- その他、その turn に必要な引数を完全に再構成する
- 新規 job と継続では `skills` を渡さない。委譲ヘッダと前回の `skills` は継承しない
- `model`、`reasoning_effort`、`fast`、`run_mode` は渡さない

同じ Codex thread でも、同じ Jules session でも、新しい `request_id` を使う。前回 job の `request_id` は使わない。Codex は質問への回答を作らない。ユーザーが次の turn で `$jules` に書いた payload だけを送る。

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
- 質問への回答を作って Jules へ送ること
- 受理不明中に別 `request_id` で再送すること
- patch の自動適用
- commit、push、fetch、rebase、checkout
- cancel を成功や cancelled として報告すること
- Facade 単発 CLI の起動
- 非対応 option を黙って省略して `start_agent` を送ること

特に、外部 agent を走らせながら Codex 側でも同じ Issue とコードを調査する動きは不正である。

## 外部 agent 結果の中継

`result.outputText` を受け取ったあと、Codex は独自回答を追加しない。不足していると思った箇所を補完しない。別の調査結果を混ぜない。外部 agent の結論を再設計しない。自分で続きを実装しない。この Skill の役割は relay である。外部 agent の結果そのものがユーザーへの主たる応答である。

### waiting_for_input

`wait_agent_job` は Jules が `waiting_for_input` になったときも戻る。これは Jules task の完了ではない。ユーザー入力待ちの settled state である。`completed` と誤認しない。

snapshot または result から `result.sessionId`、`inputPrompt`、既にある `artifacts`、あるときは `sessionUrl` を読む。Jules では snapshot 直下の `sessionId` も同じ ID である。質問をユーザーへ中継する。Codex は回答を作らない。ユーザーが次の turn で回答または明示的な継続を `$jules` に書いたとき、Follow-up continuation の新しい `start_agent` で同じ Jules session へ送る。

### submission_unknown

`status` が `running` で `remotePhase` が `submission_unknown` のとき、または `communication_error` のとき、新しい request を作って同じ作業を再送しない。同じ `jobId` を `wait_agent_job` または `get_agent_job` で再照会する。

`sendMessage` の受理不明中は session-level lock が保持される。別 `request_id` で同じ session へ再送して回避しない。Facade 再起動後も、保存済み job を同じ `jobId` から監視再開する。通信不能を remote task の失敗や停止として報告しない。

### cancel

Jules の `cancel_agent_job` は remote session の停止を保証しない。unsupported である。ユーザーが cancel を要求したら、現在の Facade 契約上 unsupported であることを報告する。cancelled になったと報告しない。`cancel_agent_job` の戻りを成功扱いしない。

### artifacts

Jules の patch artifact はローカル worktree を直接編集した結果ではない。result や snapshot の `artifacts` にある `patchPath`、`baseCommitId`、`activityId`、`createdAt` と、あるときは `sessionUrl` を必要に応じて中継する。patch を自動適用しない。最後の patch だけを全成果とは扱わない。patch が無い `completed` も、その `outputText` を中継する。
