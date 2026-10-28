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
- 継続の成果は、送信前に保存した Activity ID の baseline に無く、prompt の SHA-256 が一致する `userMessaged` がちょうど1件あるものだけを今回分として採用する。一覧順と時刻だけでは相関しない。複数件は `activity_correlation_failed` とし、session lock は残す。
- 新規 session の完了は、その session の `sessionCompleted` を要求する。create の文書は初回 prompt の `userMessaged` を保証しないため、新規 session では `userMessaged` を必須にしない。
- create の結果が不明で session ID が無い job は terminal `submission_unknown`。同じ request の retry は保存結果を返し、POST は再送しない。
- `sendMessage` の結果不明は running のまま `remotePhase=submission_unknown` とし、一意の Activity を確認するまで session lock を保持する。
- session ID を保存済みの Jules job は、Facade 再起動時の一律 `facade_interrupted` から外す。次の get / wait が監視を再開する。通信不能は remote task の失敗と分け、remote の停止とは報告しない。
- `waiting_for_input` は wait を終える settled state。回答は新しい request ID と同じ session ID の `start_agent`。
- artifact は patch、base commit、Activity ID、生成時刻を保持する。patch 本文は改変しない。最後の patch を全成果とは扱わない。patch が無い正常完了も許す。

## この環境で未実施の実測

`JULES_API_KEY` は未設定だった。接続済み Source を使った質問、回答、完了、同文再送の区別、複数 patch の実 API 観測はしていない。`agy` の soft-deny 文面も再実測していない。

自動テストは process、HTTP、Git の stub と一時ディレクトリで、上記の分岐を検証する。CI は実サービスを必須にしない。

Activity 相関が実 API で成立しない場合、Jules の継続を成功にしてはならない。実タスクの実行と、各 `baseCommitId` の別 checkout での patch 適用確認が終わるまで、Issue #40 の目的は未達である。
