# PoC 観測ログ

issue [#1](https://github.com/suusanex/codex_agent_facade/issues/1) の 8 項目。成立させること自体は成功条件ではない。迂回実装で不可を隠さない。

観測日: 2026-08-23  
環境: Windows、Codex CLI 0.149.0、GitHub Copilot CLI 1.0.80、Grok Build `grok 1.0.5 (5115b46bc9) [stable]`  
経路 A: Codex CLI (`codex exec`) → MCP `run_agent` → Facade → 各 CLI。プロジェクト `.codex/config.toml` で server 登録。  
経路 B: `src/PocSmoke.cs` から Driver 直呼び（MCP を通さない対照）。  
Desktop Codex App の composer / 通知 UI は未操作。公式には CLI と App が同じ `config.toml` の MCP 設定を共有する。

状態の意味:

- `成立` — 実機で確認できた
- `部分成立` — 経路の一部だけ確認できた
- `不可` — 現行 CLI / Codex / MCP の組み合わせでは成立しない
- `人手確認待ち` — Codex App UI など、この環境から操作できない範囲
- `実装上の境界` — コードと実測から読み取れる責務分割

| # | 観測項目 | 状態 | 根拠 |
| --- | --- | --- | --- |
| 1 | 選択 agent へ実作業 prompt を渡し、応答を返せるか | 部分成立 | Codex CLI 0.149.0 が MCP `run_agent` を呼び、同一 turn の agent_message に結果を返した。Grok `outputText=pong` / `sessionId=01a02e21-7892-7c50-9146-0c1d4f508532`。Copilot `outputText=pong` / `sessionId=cb783c8f-cd6a-4b70-84e7-4ce65598b578`。Desktop App の composer は未操作 |
| 2 | Codex 形式 Skill を各 agent へ実用的に変換できるか | 部分成立 | 共通 runtime は無い。Copilot 先頭行 `/name` は CLI slash command になり JSONL を壊す。Copilot は `Use the /name skill.`、Grok は `/name` 行。Grok は skill 探索後に invoke を報告。Copilot は `/dotnet-file-based-apps isn’t available in this CLI session` |
| 3 | 同じ外部 session へ次入力を継続できるか | 部分成立 | Codex MCP 経由で Grok に `session_id` を渡し、同一 ID で `pingpong`。Driver は `--resume` を使う（`--session-id` は既存 ID で `already in use`）。Copilot `--resume` も同一 session で `pingpong`。Desktop 継続 UI は未確認 |
| 4 | 質問・permission を Codex の入力待ちへ橋渡しできるか | 不可（この CLI 経路） | 独自 queue は無い。`auto_approve=false` でも Copilot は 45 秒以内に exit 0。Grok は `ask_user_question` が non-interactive で失敗し、質問文を最終 `text` に書いて `stopReason=end_turn`。プロセスはユーザー入力待ちにならない |
| 5 | streaming を Codex UI へ逐次表示できるか | 部分成立 | `codex exec --json` では `mcp_tool_call` が `in_progress` → `completed`。行単位の assistant 本文は最終 tool result にまとまる。Desktop の逐次表示は未確認。独自 stream プロトコルは無い |
| 6 | 数分の実行中に別 Codex thread / worktree を使えるか | 人手確認待ち | Facade は同期 MCP tool。`tool_timeout_sec=1800` をプロジェクト config に設定済み。Desktop での別 thread 操作は未確認 |
| 7 | 完了が Codex 本来の通知・thread 一覧・復帰と連動するか | 人手確認待ち | CLI では `turn.completed` まで到達。Desktop の完了通知・一覧復帰は未確認。Facade は追加通知を送らない |
| 8 | 2 Driver 間の共通 / 固有 | 実装上の境界 | 下記。Skill と session 継続フラグは実測で異なったので共通化していない |

## 実測コマンド（要約）

正常系（`auto_approve=true`）:

```text
copilot --prompt <prompt> --output-format json --allow-all [--resume <id>]
grok --no-auto-update -p <prompt> --cwd <dir> --output-format streaming-json --always-approve [--resume <id>]
```

Windows では `copilot` の拡張子なし npm shim は PE ではない。`.cmd` を優先する。

Grok は headless `streaming-json`（NDJSON）を使う。最終 `outputText` / `sessionId` は `text` chunk と `end` から復元する。tool_call / plan 等は run log へ流す。実機での長時間 tail は人手確認待ち。Copilot JSONL の session は `type=result` の `sessionId`。assistant 本文は `type=assistant.message` の `data.content`。

## 8. 共通責務と agent 固有責務

Facade 共通（2 Driver で実際に同じだったもの）:

- MCP tool `run_agent` と入力バリデーション
- agent 名による Driver 選択
- プロセス起動（cwd、引数配列、stdout/stderr、cancel 時 kill、例外トレース）
- `auto_approve` を各 CLI の公式フラグへ渡すかどうかのスイッチ

GitHub Copilot 固有:

- `copilot.cmd`、`--prompt`、`--output-format json`、`--allow-all`、`--resume`
- JSONL。`result.sessionId`。`assistant.message.data.content`
- Skill は `Use the /name skill.`（先頭 `/name` は CLI command）
- `--output-format json` でも slash command 扱いになると TUI 行が混ざる

Grok Build 固有:

- `grok`、`-p`、`--cwd`、`--output-format json`、`--always-approve`
- 継続は `--resume`。`--session-id` は既存 ID で `already in use`
- `streaming-json` の NDJSON。`text` chunk と `end.sessionId`
- Skill は `/name` 行（slash command = skill）
- non-interactive の質問は最終 text に落ち、入力待ちにはならない

意図的に共通化していないもの:

- Skill 変換、session フラグ、JSON スキーマ、permission モデル、ACP

## Streamable HTTP（issue #7）

stdio をやめ、固定 loopback の stateless Streamable HTTP を標準 transport にした。Facade process は Codex thread の lifetime から分離する。外部 agent の継続状態は従来どおり各 CLI の `session_id` / `--resume` に置き、Facade 内に durable session store は持たない。

単体テスト（実 `copilot` / `grok` なし）:

- `127.0.0.1` のみ listen
- Bearer token 必須（欠落・不一致は 401）
- 不正 Host は拒否
- 複数 MCP client が 1 host を共有し、session header 無しで `start_agent` / `get_agent_job` できる

人手確認待ち（Codex App / CLI 実機）:

- 複数 Codex thread から同じ `http://127.0.0.1:18765/mcp` を使っても Facade process が増えない
- Facade を kill → 同じ port で再起動したあと、**既存 thread を捨てずに** MCP refresh / reconnect できる
- 復旧後に同じ `session_id` で `--resume` できる
- HTTP server 再起動成功と Codex 側の自動 reconnect は別問題として記録する（`openai/codex#22571`）

## WinExe / server log / Task Scheduler 常駐

観測日: 2026-08-29。`dotnet publish src/CodexAgentFacade.cs` した `CodexAgentFacade.exe` を直接起動。

| 項目 | 状態 | 根拠 |
| --- | --- | --- |
| PE subsystem が Windows GUI（WinExe） | 成立 | Optional Header Subsystem = 2 |
| 直接起動でコンソールウィンドウが出ない | 成立（この環境） | `MainWindowHandle = 0`。対話デスクトップでの目視は人手確認として README に残す |
| loopback LISTEN | 成立 | `127.0.0.1:18766` LISTEN（スモークは既定 port を占有しないため 18766） |
| `server.log` 生成 | 成立 | `%USERPROFILE%\.codex-agent-facade\server.log` に Starting / Listening / Application started |
| 無認証は 401、token は log に出ない | 成立 | POST `/mcp` が 401。スモーク用 token の平文は `server.log` に無い |
| start_agent → get_agent_job | 成立 | 実 `grok-build`。`status=completed`、`outputText=pong` |
| 終了後の再起動 | 成立 | Kill 後に同じ exe を起動し、同じ port が再 LISTEN |
| agent 詳細が server.log に重複しない | 成立 | Grok の `available_commands` / stdout 断片は `server.log` に無い（run log 側） |

Windows Service 化はしていない。Task Scheduler からの登録自体は人手作業。

## Run log（issue #4）

Codex UI 上の逐次本文表示は引き続き部分成立 / 人手確認待ち。Facade は invocation ごとに `%USERPROFILE%\.codex-agent-facade\runs\` へ `{runId}.events.jsonl` と `{runId}.log` を書く（`CODEX_AGENT_FACADE_LOG_DIR` で上書き可）。MCP progress とは別経路。単体テストでファイル生成・heartbeat・Grok NDJSON の tool 要約は確認済み。実 CLI を `Get-Content -Wait` で追う観測は人手確認待ち。run log は観測専用のまま。

## Durable job（issue #3）

### 設計判断

Issue 原文は「実装は設計判断のあと」と書いていた。HTTP 化（#7）後も `run_agent` は MCP `tools/call` の CT で CLI process tree を殺す。これはコード上の結合であり、Desktop の別 thread / 通知の人手確認を待たなくても、blocking RPC では接続断・timeout から復旧できない。blocking は採用しない。最小 lifecycle をこの issue で実装する。

| 項目 | 判断 |
| --- | --- |
| blocking MCP call | 不採用。`start_agent` と `get_agent_job` に分離する |
| 最小 contract | 必須 `request_id`、`start_agent` / `get_agent_job` / `cancel_agent_job`。`get` は再実行しない |
| MCP Tasks | 使わない。Codex 公式 MCP 文書（2026-08-27）は STDIO / Streamable HTTP / Bearer / OAuth / `tool_timeout_sec` のみ。`io.modelcontextprotocol/tasks` の client opt-in は確認できない。C# SDK の `ModelContextProtocol.Extensions.Tasks` は server 側だけあっても、opt-in 無しでは `CreateTaskResult` を返してはならない |
| process ownership | Codex は Facade を所有しない。Facade process が in-process worker と CLI 子プロセスを所有する。Windows では Job Object の `KILL_ON_JOB_CLOSE` で Facade 終了時に CLI process tree を落とす。独立 worker process は作らない |
| 永続化 | 実行中 worker はメモリ。`request_id`・jobId・status・terminal result は `%USERPROFILE%\.codex-agent-facade\jobs\`。外部 session は CLI の `session_id` / `--resume`（Facade は第 2 session store にしない） |
| 再取得 | MCP 切断後は同じ Facade 上で `request_id` 再 start または `jobId` で get。完了 result は disk から再取得できる |
| crash / 再起動 | 実行中 job は fail-closed で `failed`（facade process exited）。同じ `request_id` で再実行しない。やり直すなら新しい `request_id` |
| WAITING_FOR_INPUT | 実装しない。PoC #4 どおり現行 CLI は質問待ちプロセスにならない |
| streaming | MCP progress は短命 RPC には載せない。観測は run log |

将来 Codex が `io.modelcontextprotocol/tasks` を request `_meta` で opt-in するようになったら再評価する。そのときの境界は `AgentJobService`（core lifecycle）と `AgentTools`（MCP adapter）。Driver / ProcessRunner は変えない。

状態の対応（独自 JSON → Tasks）:

| 本実装 | MCP Tasks |
| --- | --- |
| `running` | `working` |
| （未使用） | `input_required` |
| `completed` | `completed` |
| `failed` | `failed` |
| `cancelled` | `cancelled` |
| `request_id` | 独自。Tasks の taskId は server 生成 |

単体テスト（実 `copilot` / `grok` なし）:

- プロセス完了前に `jobId` を返す
- 別 MCP client から running を poll できる
- 同じ `request_id` では CallCount が増えない
- unknown `jobId` は tool error
- `cancel_agent_job` が runner CT を cancel する
- Facade 再起動相当のあと、完了 result を同じ `request_id` で再取得し、実行中だった job は fail-closed で再実行しない

人手確認待ち（Codex App / CLI 実機）:

- `start_agent` の tool result を取り損ねたあと、同じ `request_id` で再試行して同じ `jobId` を得る
- 接続復旧後に `get_agent_job` で実行中または完了結果を得る
- 完了後の `session_id` で `--resume` できる
- Desktop で別 thread へ移っても job が継続する（観測 #6 の残り）

## Devin CLI（issue #10）

issue [#10](https://github.com/suusanex/codex_agent_facade/issues/10)。既存 MCP job 経路へ第三 Driver として `devin-cli` を追加する。`devin acp` は使わず `devin --print` から始める。Devin 独自の workspace 管理層は追加しない。

観測日: 2026-08-29  
環境: Windows。単体テストは実 `devin` なし。公式 CLI 文書（Commands / Permissions / Skills / Self-serve plans）に基づく机上マッピング。

qualification 1 の到達点（blocked）:

- `Get-Command devin` は失敗。PATH に `devin` が無い
- 公式 Windows 導入 `irm https://static.devin.ai/cli/setup.ps1 | iex` は OS error 5（アクセスが拒否されました）で失敗した。この task から CLI を導入できない
- そのため `devin --help`、未認証の `devin --print`、Free / 認証境界の実エラー本文は取得できていない
- 以降の disposable 編集、MCP job 経路、session、Skill はすべて未実施

人手での作業が必要: 十分な権限で Devin CLI を導入し、`devin auth login` する。導入後に残る確認は下記 checklist。

状態の意味は冒頭の定義に従う。

| # | 観測項目 | 状態 | 根拠 |
| --- | --- | --- | --- |
| 1 | Codex から `devin-cli` を選び、指定 worktree で作業できるか | 人手確認待ち | Facade routing / stub runner / `start_agent` の agent 識別子は実装済み。この環境に `devin` が無く、実 `devin --print` と Codex App e2e は未実施 |
| 2 | 既存 job lifecycle / cancel / run log を壊さないか | 部分成立 | 既存 job テストは `DevinCliDriver` を含む `AgentFacade` コンストラクタで維持。実 CLI の cancel は人手確認待ち |
| 3 | 完了後に最終応答を `get_agent_job` の result として返せるか | 人手確認待ち | Driver は assistant JSON があればそれを、無ければ plain text を `outputText` にする。`--print` の実形式は未確認のため、最終応答の安定抽出は未成立 |
| 4 | 同一 Devin session を `--resume` で継続できるか | 不可（この環境では未成立） | 引数は `--resume <session_id>`。`--continue` は並列取り違えのため使わない。session ID は明示 JSON フィールドだけ。stdout に ID が無い場合の `--export` は未実装。実出力を見ていないため継続は qualification 未成立 |
| 5 | `.agents/skills/` を利用した指示ができるか | 人手確認待ち | Driver は Codex Skill 名を `/name` 行へ前置する。実 invoke は未確認 |
| 6 | `auto_approve` を permission mode へ対応できるか | 実装上の境界 | `true` のとき `--permission-mode dangerous`。`false` のときは付けない（既定 `normal`）。`autonomous` は `--sandbox` 必須で native Windows では使わない。質問待ち橋渡しは作らない |
| 7 | Free plan / trial で CLI が使えるか | 不可（この環境では未到達） | CLI 導入が OS error 5 で blocked。Free 制限の実エラーは未取得。**作業仮説:** Free の説明に Devin CLI は無く、Pro の quota 説明に Devin CLI がある |
| 8 | ACP が必要か | 実装上の境界 | 最初の成立条件は `--print` の最終応答と既存 process log。structured event が必要になるまで ACP は導入しない |

### 机上の CLI 変換

```text
devin --respect-workspace-trust false [--permission-mode dangerous] [--resume <session_id>] --print -- <prompt>
```

- `--respect-workspace-trust false` は非対話 `--print` が workspace trust prompt を出せないための公式条件。常に付ける
- working directory は `ProcessRunner` の cwd。`--cwd` は付けない
- Skill 変換は Grok と同じ `/name` 行だが、共通 runtime にはしない
- stdout が JSON でなくても成立とする。明示 `sessionId` フィールドだけを session 継続に使う。任意 UUID や `devin list` の時刻差は使わない

単体テスト（実 `devin` なし）:

- `BuildArguments` の non-interactive flags / `auto_approve=false` / `--resume` / Skill 前置
- Facade が `FileName=devin` へルーティングする
- plain text stdout と JSON 行の sessionId / text
- 空 stdout・非 0 exit・任意 UUID を sessionId にしないこと

人手確認待ち（qualification 1 が OS error 5 で blocked。以降は CLI 導入後）:

1. 十分な権限で Devin CLI をインストールし `devin auth login` する。公式スクリプトはこの環境で Access Denied になった
2. Facade を介さない `devin --print` の最小応答と stdout / stderr 形式。Free で遮られる場合はそのエラー本文を記録する
3. disposable worktree での小さなファイル編集
4. 既存 MCP job 経路（`start_agent` / `get_agent_job` / `cancel_agent_job`）で同じ編集と run log
5. Codex App → MCP → Facade → Devin CLI の e2e
6. session ID。stdout に明示フィールドが無ければ `--export` を run 固有パスへ足すか、継続不能と確定する。`devin list` と時刻差は使わない
7. `.agents/skills/` の実 invoke
8. Free / trial でどこまで通ったか、課金が必要だった境界

有料契約後に残る最小確認（1 が課金で遮られた場合）:

- 非対話 `--print` が最終応答を stdout に出すこと
- `working_directory`（process cwd）でファイル編集できること
- `get_agent_job.result.outputText` に最終応答が載ること
- 可能なら `--resume` と Skill invoke

