# codex_agent_facade

Codex App を薄い UI shell として、GitHub Copilot と Grok Build へ作業を中継する feasibility PoC。

Codex / Facade は planner や orchestrator にならない。ユーザーの prompt を構造化 MCP 入力として受け、選択した agent の CLI へ変換して実行し、応答を同じ Codex thread へ返す。

## 必要環境

- .NET 11 SDK（Preview 可）。`#:include` で複数ファイルをコンパイルする
- PATH 上の `copilot`（GitHub Copilot CLI）および / または `grok`（Grok Build CLI）
- 実作業には各 CLI へのログインが必要

このリポジトリは File-based apps を使う。`.csproj` は無い。

## 起動

Facade は Codex の子プロセスとしては起動しない。先に 1 プロセスを立て、複数の Codex thread がその loopback endpoint を共有する。

ユーザー環境に Bearer token を置く。空だと起動に失敗する。

```powershell
[System.Environment]::SetEnvironmentVariable("CODEX_AGENT_FACADE_TOKEN", "<secret>", "User")
$env:CODEX_AGENT_FACADE_TOKEN = "<secret>"
dotnet run --file src/CodexAgentFacade.cs
```

既定の listen 先は `http://127.0.0.1:18765/mcp`。`127.0.0.1` のみに bind する。ポートを変える場合は `CODEX_AGENT_FACADE_PORT` と Codex 側の `url` を同じ値に揃える。token はログに出さない。

`dotnet publish` した exe でもよい。exe を使う場合は成果物フォルダごと配置し、ソースツリーは不要。publish 成果物は WinExe なので、直接起動してもコンソールウィンドウは出ない。

```powershell
dotnet publish src/CodexAgentFacade.cs
```

ポートが既に使われている場合は別ポートへ逃げず、起動失敗する。

## Server log

server / host 自身の診断ログは run log とは別ファイルへ書く。既定:

```text
%USERPROFILE%\.codex-agent-facade\server.log
```

`runs\` と `jobs\` には書かない。Grok / Copilot の stdout や agent run の詳細は従来どおり run log の責務である。

- 既定レベル: `Information` 以上
- 1 ファイル最大 1 MB
- archive 最大 4 世代（`server.1.log` 形式。NLog の File.Move archive）
- 総容量は数 MB 程度で打ち止め
- 設定はコード固定。外部 `NLog.config` は無い

起動・listen・停止・bind 失敗・token 不備・MCP / ASP.NET Core の警告・エラー・Facade 内部の重要な例外を残す。コンソールや `System.Diagnostics.Trace` には依存しない。

## Windows 常駐（Task Scheduler）

Facade は Windows Service にしない。GitHub Copilot CLI / Grok Build CLI のユーザー認証を使うため、対象ユーザーのログオンセッション内で `CodexAgentFacade.exe` を直接起動する。PowerShell や `Start-Process` の wrapper は不要。

1. `CODEX_AGENT_FACADE_TOKEN` を対象ユーザーのユーザー環境変数として設定する
2. `dotnet publish src/CodexAgentFacade.cs` したフォルダごと配置する（例: `D:\Tools\Development\CodexAgentFacade\`）
3. タスク スケジューラで基本タスクではなく「タスクの作成」から登録する

推奨設定:

| 項目 | 値 |
| --- | --- |
| セキュリティ オプション | 「ユーザーがログオンしているときのみ実行する」 |
| トリガー | 対象ユーザーのログオン時 |
| 操作 | プログラム `D:\Tools\Development\CodexAgentFacade\CodexAgentFacade.exe`。引数なし。開始場所は exe と同じフォルダ |
| 既に実行中の場合 | 新しいインスタンスを開始しない |
| 停止条件 | 長時間実行前提なので、タスクを停止する期限は設定しない |
| 異常終了 | 必要ならタスク スケジューラの再起動機能を使う |

exe は GUI subsystem（WinExe）なので、ログオン時にコンソールウィンドウは出ない。起動・停止・異常終了はタスク スケジューラがプロセスとして追跡する。

人手確認: 配置した `CodexAgentFacade.exe` を直接起動し、コンソールウィンドウが出ないこと、`127.0.0.1:18765` が LISTEN になること、`server.log` が生成されることを確認する。

## Codex への接続

`~/.codex/config.toml` または信頼済みプロジェクトの `.codex/config.toml` に追加する。

別リポジトリを編集するときは、編集対象ではなく **ユーザー設定** `~/.codex/config.toml` に書く。

`/mcp` ではこの server を有効化できない。設定ファイルに最初から `enabled = true` を書く。

`start_agent` / `get_agent_job` / `cancel_agent_job` は短時間の MCP RPC である。長時間の agent 実行は job として Facade process 内で継続する。`direct_only_tool_namespaces` を指定しないと、Codex 側が進捗確認とタイムアウトを行い、期待どおり完了しない。いずれもホスト側設定であり、Facade の迂回実装ではない。

Facade を再起動したあと、Codex が自動 reconnect するとは限らない。その場合は既存 thread を捨てず、Codex 側の MCP refresh / reconnect を行う。

```toml
[features.code_mode]
direct_only_tool_namespaces = ["mcp__codex_agent_facade"]

[mcp_servers.codex_agent_facade]
url = "http://127.0.0.1:18765/mcp"
bearer_token_env_var = "CODEX_AGENT_FACADE_TOKEN"
startup_timeout_sec = 30
tool_timeout_sec = 60
default_tools_approval_mode = "auto"
enabled = true
```

`url` は `localhost` ではなく `127.0.0.1` を使う。Codex App では設定を保存したあと Restart する。

## MCP tool

公開 tool は `start_agent` / `get_agent_job` / `cancel_agent_job`。blocking な `run_agent` は無い。

作業ごとに呼び出し側が `request_id` を一度生成して保持する。`start_agent` の結果を取り損ねたら、**同じ `request_id`** で再試行する。新しい id で打ち直すと別 job になる。

### `start_agent`

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `request_id` | はい | 呼び出し側が生成する冪等キー。同じ値の再呼び出しは既存 job を返す |
| `agent` | はい | `github-copilot` または `grok-build` |
| `prompt` | はい | 対象 agent へ渡す本文。Facade は再構成しない |
| `working_directory` | はい | 対象 workspace / worktree |
| `session_id` | いいえ | 同一外部 session の継続。省略時は新規 |
| `skills` | いいえ | Codex 形式の Skill 名。Driver ごとに native 形式へ変換する |
| `auto_approve` | いいえ | 既定 true。各 CLI の non-interactive 承認フラグを付ける。質問待ちの観測では false |

戻り JSON:

- `jobId`
- `requestId`
- `status`（`running` / `completed` / `failed` / `cancelled`）
- `pollAfterMs`

同じ `request_id` で入力が違う場合は tool error。既存 job は継続する。MCP 接続が切れても job は止まらない。

### `get_agent_job`

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `job_id` | はい | `start_agent` が返した `jobId` |

何度呼んでも agent を再実行しない。不明な `jobId` は tool error。`completed` のとき `result` に次を含む。

- `agent`
- `sessionId`（CLI が明示した session フィールド、または Copilot の `--resume=` hint。任意 UUID は使わない。読めなければ空）
- `exitCode`
- `outputText`
- `rawOutput`
- `runId`（`jobId` と同じ）
- `eventsLogPath`
- `textLogPath`

`failed` / `cancelled` では `error` を返す。失敗時はフォールバックせず MCP tool error になる。

### `cancel_agent_job`

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `job_id` | はい | `start_agent` が返した `jobId` |

実行中なら CLI process tree を止める。既に完了している job は状態を変えない。MCP 切断だけでは cancel しない。

実行中の worker は Facade process 内にある。Windows では子 CLI を Job Object（`KILL_ON_JOB_CLOSE`）へ入れる。Facade プロセスが落ちると CLI も終了する。独立した worker process は持たない。

`request_id` と terminal な job snapshot は `%USERPROFILE%\.codex-agent-facade\jobs\` へ残す。Facade 再起動後に同じ `request_id` で `start_agent` しても **新しい agent は起動しない**。

- 完了済みなら保存してある result を返す
- 実行中だった job は fail-closed で `failed`（error: facade process exited）。同じ作業をやり直すときは新しい `request_id` を使う
- 外部 agent の会話継続は従来どおり completed `result.sessionId` / `--resume`

Codex が `start_agent` の応答だけを取り損ねた場合（Facade は生きている）は、同じ `request_id` で再試行すれば実行中の同じ `jobId` が返る。

## Run log

Codex UI へのストリーミング表示とは独立して、各 agent job の逐次出力を Facade 専用ディレクトリへ保存する。対象リポジトリの working tree は使わない。run log は観測用であり、`get_agent_job` の代わりにはならない。

既定の保存先:

```text
%USERPROFILE%\.codex-agent-facade\runs\
```

環境変数 `CODEX_AGENT_FACADE_LOG_DIR` が空でなければ、そのディレクトリを優先する。`%USERPROFILE%` などの環境変数は展開する。相対パスはユーザープロファイル基準で正規化する。

1 invocation につき一意な `runId` を付け、同じ ID で次の 2 ファイルを同時に append する。

| ファイル | 用途 |
| --- | --- |
| `{runId}.events.jsonl` | 機械解析・監査向け。agent の構造化イベントを元の粒度のまま保存する。Facade の started / heartbeat / completed / failed / cancelled も含む |
| `{runId}.log` | 人間が実行中に読むテキスト。thought / assistant の細かい streaming fragment は読みやすい行へ結合する。tool 概要 / plan / 完了も含む。巨大な tool 入出力はここに展開しない |

実行中の追従例:

```powershell
Get-Content -Wait "$env:USERPROFILE\.codex-agent-facade\runs\<runId>.log"
```

`runId` は `jobId` と同じ値である。パスは completed の `result` に含まれる。heartbeat は 15 秒間隔で、経過時間・process 生存・最後の外部出力からの経過を記録する。出力が無いこととハングは同義ではない。認証情報・credential・token は書き込み前に `[REDACTED]` へ置換する。起動時には PATH 解決後の実行ファイルと、Windows で `.cmd` を `cmd.exe` 経由にしたかどうかも残す。

## 編集対象リポジトリへの Skill 導入

Skill は **編集する work repository** の root で APM から入れる。この Facade リポジトリへ入れる必要はない。MCP server は APM では入らない。

```powershell
apm install suusanex/codex_agent_facade/apm-packages/github-copilot --target codex,agent-skills
apm install suusanex/codex_agent_facade/apm-packages/grok-build --target codex,agent-skills
```

ローカル checkout から入れる場合:

```powershell
apm install "C:\path\to\codex_agent_facade\apm-packages\github-copilot" --target codex,agent-skills
apm install "C:\path\to\codex_agent_facade\apm-packages\grok-build" --target codex,agent-skills
```

展開先は `.agents/skills/github-copilot/` と `.agents/skills/grok-build/`。Codex 上では `$github-copilot` / `$grok-build` で本文を外部 agent へ渡す。Skill 無しで `start_agent` / `get_agent_job` を直接呼んでもよい。

更新・削除:

```powershell
apm update
apm uninstall github-copilot
apm uninstall grok-build
```

## CLI 変換

GitHub Copilot（プロセス cwd = `working_directory`）:

```text
copilot --prompt <prompt> --output-format json [--allow-all] [--resume <session_id>]
```

Grok Build:

```text
grok --no-auto-update -p <prompt> --cwd <working_directory> --output-format streaming-json [--always-approve] [--resume <session_id>]
```

`--allow-all` / `--always-approve` は `auto_approve=true` のときだけ付ける。Windows では `copilot.cmd` を使う（拡張子なし npm shim は起動できない）。

Skill 変換は共通化しない。Copilot は `Use the /name skill.`、Grok は `/name` 行。詳細は `docs/poc-observations.md`。

## テスト

CI / 通常テストは実 `copilot` / `grok` を呼ばない（`dotnet --version` の収集確認だけ実プロセスを使う）。

```powershell
dotnet run --file tests/CodexAgentFacade.Tests.cs
```

実 CLI 観測用ハーネス（公開 MCP I/F ではない）:

```powershell
dotnet run --file src/PocSmoke.cs
```

## 観測

PoC の成果物は実装に加え、成立 / 不可の記録である。`docs/poc-observations.md` を更新する。

## 人手での作業が必要

- `CODEX_AGENT_FACADE_TOKEN` をユーザー環境に設定し、Facade プロセスを事前起動する（Windows では Task Scheduler から `CodexAgentFacade.exe` を直接起動してよい）
- 常駐 exe を直接起動したときコンソールウィンドウが出ないことの確認（WinExe。自動テストでは検証しない）
- GitHub Copilot CLI と Grok Build CLI へのログイン
- Codex への MCP 登録（`url`、`bearer_token_env_var`、`enabled = true`、`direct_only_tool_namespaces`）
- Facade 再起動後に自動 reconnect しない場合の、同一 thread 上での MCP refresh / reconnect
- Desktop Codex App の composer / 完了通知 / 別 thread 並行（HTTP 移行後の start/get 実機確認は `docs/poc-observations.md`）
