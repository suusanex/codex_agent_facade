# Antigravity / Jules

Issue [#40](https://github.com/suusanex/codex_agent_facade/issues/40) の実装記録。専用 MCP tool と汎用 provider framework は追加していない。既存の `start_agent` / `wait_agent_job` / `get_agent_job` / `cancel_agent_job` に `agent=antigravity|jules` を足した。

## 確認できた資料

- Antigravity CLI の headless 仕様: [Headless](https://antigravity.google/docs/cli/headless/)。`agy -p`、`--output-format stream-json`、`--print-timeout`、`--conversation`、`--model`、`--effort`、`--dangerously-skip-permissions` を使う。未指定の option は Facade が補完しない。
- stream は `init`、`step_update`、`result` を読む。成功は result status `SUCCESS` と process の正常終了の両方。`ERROR` / `INVALID` は失敗、`WAITING` / `RUNNING` は未完了、`CANCELED` / `INTERRUPTED` は Facade のキャンセルと結び付くときだけ cancelled。
- permission soft-deny は、stderr または tool event に `auto-denied` があること、または `denied_actions` が空でない配列であることを `completionReason=permission_soft_denied` にする。この文言は公開 changelog の記述であり、この作業環境で `agy` 1.4.9 を再実測した記録ではない。
- Jules は `https://jules.googleapis.com/v1alpha` の Source、Session、Activity API。[Session](https://jules.google/docs/api/reference/sessions/)、[Activity](https://jules.google/docs/api/reference/activities/)、[型](https://jules.google/docs/api/reference/types/)、[Source](https://jules.google/docs/api/reference/sources/)。
- 通信は BCL の `HttpClient` と `System.Text.Json`。Google Labs の TypeScript SDK は別 runtime になるため使っていない。
- API key は process 環境変数 `JULES_API_KEY` だけ。公開引数、job store、ログへは保存しない。未設定でも他の agent は起動する。
- `sendMessage` の成功応答は空になり得る。DELETE は実行停止の保証が文書に無いため、cancel では呼ばない。`cancel_agent_job` は副作用なしの unsupported を返す。
- 新規 session は `requirePlanApproval=false` で、自動 PR 作成は指定しない。`automationMode` は送らない。

## 実装した境界

- Antigravity は MCP と単発 CLI。Jules は MCP のみ。単発 CLI は process を起動する前に拒否する。
- 両 agent は `run_mode`、`fast`、空でない `skills` を拒否する。Jules はさらに明示的な model、reasoning effort、`auto_approve=false` を拒否する。
- 新規 Jules session は clean worktree だけを受ける。attached branch は configured upstream の tip が HEAD と一致するときだけ。detached HEAD は `git ls-remote` で HEAD と一致する一意の branch だけ。commit / push / fetch / checkout はしない。
- 継続は Session の Source とローカル repository の一致だけを見る。dirty や branch の進行では拒否しない。継続できる state は `AWAITING_USER_FEEDBACK` だけ。
- 継続の成果は、送信前に保存した Activity ID の baseline に無く、prompt の SHA-256 が一致する `userMessaged` がちょうど1件あるものだけを今回分として採用する。一覧順と時刻だけでは相関しない。複数件は `activity_correlation_failed` とし、session lock は残す。完了済み session に該当する `userMessaged` がまだ無い場合は terminal にせず、running の `submission_unknown` と session lock を維持する。次の get / wait が同じ job を再照会する。
- session GET が成功しているときの activities 一覧 404 は、一覧がまだ無いものとして poll を続ける。session 自体の 404 は `remote_rejected` である。session ID を保存したあとの 401/403 や、2xx でも JSON を読めない read は `communication_error` の running のままにする。remote task の FAILED にはしない。
- `sessionCompleted` activity がある COMPLETED は、その場で完了する。activity が無くても session state が COMPLETED のまま activity 集合が次の poll で変わらなければ、今回分の agent message で完了する。create の文書は初回 prompt の `userMessaged` を保証しないため、新規 session では `userMessaged` を必須にしない。
- create の 2xx で body が空、JSON を読めない、または session ID を確定できない job は terminal `submission_unknown`。4xx の create は `remote_rejected`。同じ request の retry は保存結果を返し、POST は再送しない。
- `sendMessage` の結果不明は running のまま `remotePhase=submission_unknown` とし、一意の Activity を確認するまで session lock を保持する。session が先に `COMPLETED` になり、該当する `userMessaged` がまだ無い場合も同じである。
- 継続の開始は、同じ session ID を持つ未 settled の Jules job、または `SessionLockHeld` の job があると `session_locked` になる。create が session ID を公開したあとの running も対象である。lock file の owner 文字列だけでは拒否しない。settled で lock を持っていない古い owner は、次の送信が上書きする。lock を外すときは lock file を先に空にし、その後 `SessionLockHeld=false` を保存する。
- session ID を保存済みの Jules job は、Facade 再起動時の一律 `facade_interrupted` から外す。次の get / wait が監視を再開する。通信不能は remote task の失敗と分け、remote の停止とは報告しない。
- `waiting_for_input` は wait を終える settled state。回答は新しい request ID と同じ session ID の `start_agent`。
- artifact は patch、base commit、Activity ID、生成時刻を保持する。patch 本文は改変しない。最後の patch を全成果とは扱わない。patch が無い正常完了も許す。
- diff の無い `gitPatch` は保存しない。実測では progress 中の activity が `baseCommitId` だけで届いた。diff があるのに base commit が無いもの、または `createTime` が無いものは `artifact_persist_failed` とする。message の受理は解決済みなので、この terminal 失敗では session lock を外す。

## 2026-10-29 の Jules 実測

接続済み Source は `suusanex/coding_agent_plan_and_verify_process` だけだった。使い捨て branch は `jules-smoke-20261029`、先端は `3bcee41296b05b63e7bf3f7959342747712eea5a`。API key は記録していない。応答の抜粋は [fixtures](fixtures/jules-20261029/) にある。

- session `3295034226344094016` は質問のあと `SAME-TEXT-MARKER` を1件受け、user activity `task-3295034226344094016-activity-step-6896993502352719799` が付いた。その後 session state は `COMPLETED` になったが、`sessionCompleted` activity は無かった。Facade を止めたあとの `get` / `wait` が同じ job `20261029T094851Z-eac97c6e` を再送せず `completed` にし、出力は今回の agent message だけだった。patch は無い。完了済み session なので同じ文の2回目は送っていない。
- create の応答を保存する前に process が死んだ request `req-jules-same-text-session` は、再起動後 `submission_unknown` になり、POST は再送していない。一覧で見えた session `14644344499652563347` は `AWAITING_USER_FEEDBACK` だった。
- その session に同じ文 `SAME-TEXT-MARKER` を2回送った。user activity は `task-14644344499652563347-activity-step-7668377535958361935` と `task-14644344499652563347-activity-step-5426814382443254259` で、本文は同じでも ID は別だった。各回の公開結果は新しい agent message「復唱の準備はできていますか」だけで、どちらも `waiting_for_input` だった。
- session `12231436768692532422` で `jules-smoke/one.txt` に `ONE` を作らせ、質問待ちになった。保存した patch は2件で、どちらも base `3bcee41296b05b63e7bf3f7959342747712eea5a`、Activity は `a4fd27c99bbe43a5a3abe02de0c26e92` と `f27fda80bbea4b868f68332ea81e15b6`。別 checkout で両方とも適用でき、内容は `ONE` だった。
- 続けて `jules-smoke/two.txt` に `TWO` を作らせた。progress 中の `gitPatch` が `baseCommitId` だけで届き、その job は `artifact_persist_failed` になった。失敗前に保存できた10件は、同じ base の別 checkout で全部適用できた。前半は `one.txt=ONE` だけ、後半は `one.txt=ONE` と `two.txt=TWO` だった。最後の patch だけを成果にはしていない。session 自体はその後 `COMPLETED` になった。
- session `14644344499652563347` へ `RESTART-MARKER` を送った job `20261029T112102Z-e0e23374` は、`polling` で session ID を保存したあと host を止めた。別の host が同じ job を再送せず追跡し、`completed` になった。user activity は `task-14644344499652563347-activity-step-14685163969043484571` の1件だけだった。出力は「回答を受け取りました。指示通り、セッションは完了せずにこのまま待機します。何か他にご用件はありますか？」で、session state は再び `COMPLETED` だった。
- `agy` 1.4.9 の soft-deny 文面は、この環境では再実測していない。

自動テストは process、HTTP、Git の stub と一時ディレクトリで行う。2026-10-29 の実行は 454 件中 453 件が成功し、1 件は Grok の live テストをスキップした。CI は実 Jules と実 `agy` を起動しない。
