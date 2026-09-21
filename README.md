# codex_agent_facade

Codex App から GitHub Copilot、Grok Build、Cursor CLI へ作業を中継する feasibility PoC。

Facade 自身は planner や orchestrator にならない薄い execution transport である。呼び出し側（Codex の親エージェントなど）は作業の計画・分割・委譲方針を決め、自己完結した worker prompt を `start_agent` に渡せる。元の user prompt 全体を転送する必要はない。Facade はその worker task を計画・分割・意味的に書き換えない。caller が明示した `skills` などの structured option は、対応 Driver が agent 固有の呼び出し表現へ変換する場合がある。選択した agent の CLI へ変換して実行し、完了結果を返す。

## 必要環境

- .NET 11 SDK（Preview 可）。`#:include` で複数ファイルをコンパイルする
- PATH 上の `copilot`（GitHub Copilot CLI）、`grok`（Grok Build CLI）、および / または `cursor-agent`（Cursor CLI）
- Windows で Cursor CLI を使う場合は PowerShell 7（`pwsh.exe`）を PATH 上に配置する。Facade の `.ps1` 起動経路は PowerShell 7 を使用する
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

MCP tool の呼び出しも `server.log` に記録する。`start_agent` / `wait_agent_job` / `get_agent_job` / `cancel_agent_job` について、入口・正常終了・失敗の各イベントに tool 名、`invocationId`、`requestId` または `jobId`、status、`durationMs` などを記録する。job の terminal transition は `JOB` イベントとして同じ `jobId` を記録するため、次のように MCP call と完了時刻を時系列で追跡できる。

```text
MCP tool=start_agent phase=completed ... jobId=... agent=grok-build status=running terminal=false durationMs=...
MCP tool=wait_agent_job phase=completed ... jobId=... status=completed terminal=true durationMs=...
JOB phase=completed jobId=... status=completed exitCode=0
```

prompt、agent の回答本文、result本文、credential、token、その他の大きなtool payloadは `server.log` に記録しない。長時間jobの調査では、同じ `jobId` の `wait_agent_job` / `get_agent_job` の回数・時刻・最後のstatusと、`JOB` terminalイベントの時刻を比較する。

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

`start_agent` / `get_agent_job` / `cancel_agent_job` は短時間の MCP RPC である。`wait_agent_job` は最大 `timeout_seconds` までブロックするが、worker の寿命とは独立である。wait の cancel・切断・timeout だけでは worker を止めない。長時間の agent 実行は job として Facade process 内で継続する。`direct_only_tool_namespaces` を指定しないと、Codex 側が進捗確認とタイムアウトを行い、期待どおり完了しない。`tool_timeout_sec` は wait の実用値（300 秒）より先に host 側 timeout が発火しない値にする。いずれもホスト側設定であり、Facade の迂回実装ではない。

Facade を再起動したあと、Codex が自動 reconnect するとは限らない。その場合は既存 thread を捨てず、Codex 側の MCP refresh / reconnect を行う。

```toml
[features.code_mode]
direct_only_tool_namespaces = ["mcp__codex_agent_facade"]

[mcp_servers.codex_agent_facade]
url = "http://127.0.0.1:18765/mcp"
bearer_token_env_var = "CODEX_AGENT_FACADE_TOKEN"
startup_timeout_sec = 30
tool_timeout_sec = 1800
default_tools_approval_mode = "auto"
enabled = true
```

`url` は `localhost` ではなく `127.0.0.1` を使う。Codex App では設定を保存したあと Restart する。

## 発行と差し替え

`tools/publish-facade.local.example.json` を `tools/publish-facade.local.json` にコピーし、発行先のフルパスを設定する。この設定ファイルは `.gitignore` 対象である。Facadeタスクを停止し、publish成果物全体を日時付きバックアップへ退避して差し替え、同じタスクを起動する。

```powershell
dotnet run --file tools/publish-facade.cs
```

設定ファイルを別の場所に置く場合は、1個目の引数にパスを指定する。Facadeの複数インスタンスがある場合、スクリプトは停止せず終了する。

## MCP tool

公開 tool は `start_agent` / `wait_agent_job` / `get_agent_job` / `cancel_agent_job`。blocking な `run_agent` は無い。通常経路は `start_agent` のあと `wait_agent_job` で terminal まで待つ。`get_agent_job` は明示的な状態照会・復旧・診断用であり、長時間 worker の短周期 poll には使わない。

distinct な agent job / distinct な `start_agent` ごとに、呼び出し側が新しい `request_id` を生成して保持する。同じ `start_agent` の結果を取り損ねた再試行だけ、**同じ `request_id`** を再利用する。別の worker 作業には新しい id を使う。新しい id で打ち直すと別 job になる。同じ Codex thread や同じ外部 agent `session_id` を継続することは、`request_id` の再利用理由にならない。新しいユーザー turn / 新しい payload では新しい `request_id` を生成し、会話継続は completed `result.sessionId` を `session_id` に渡す。

`start_agent` は呼び出しごとに完全な引数セットを渡す RPC である。前回の `working_directory` 等は MCP / Facade 側で暗黙継承されない。continuation でも `request_id`, `agent`, `prompt`, `working_directory` を毎回指定する。

### `start_agent`

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `request_id` | はい | この distinct な agent job 用の冪等キー。呼び出し側が job ごとに新しく生成する。同じ Codex thread / 同じ外部 session でも新しい payload なら新しい値。同じ値の再呼び出しは、引数が完全一致する lost-result retry のときだけ既存 job を返す |
| `agent` | はい | `github-copilot`、`grok-build`、または `cursor` |
| `prompt` | はい | 呼び出し側が構成した自己完結の worker prompt。元の user prompt 全体である必要はない。Facade はこの task payload を再解釈しない。完全一致の転送は保証せず、`skills` 指定時は対応 Driver が agent 固有の skill 指示を付加する場合がある |
| `working_directory` | はい | 対象 workspace / worktree。continuation でも毎回指定する。前回値は暗黙継承されない |
| `session_id` | いいえ | 同一外部 session の継続。省略時は新規 |
| `skills` | いいえ | Codex 形式の Skill 名（任意）。GitHub Copilot と Grok Build は agent 固有の prompt 指示へ変換する。Cursor は現在このフィールドを変換しない。Cursor で Skill を明示 invoke する場合は worker prompt 本文へ含める |
| `auto_approve` | いいえ | 既定 true。各 CLI の non-interactive 承認フラグを付ける。質問待ちの観測では false |

戻り JSON:

- `jobId`
- `requestId`
- `status`（`running` / `completed` / `failed` / `cancelled`）

同じ `request_id` で入力が違う場合は tool error。既存 job は継続する。MCP 接続が切れても job は止まらない。

### `get_agent_job`

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `job_id` | はい | `start_agent` が返した `jobId` |

何度呼んでも agent を再実行しない。不明な `jobId` は tool error。`completed` のとき `result` に次を含む。CLI の raw stream 全体（`rawOutput`）は既定では返さない。詳細確認は既存の run log を使う。

- `agent`
- `sessionId`（CLI が明示した session フィールド、または Copilot の `--resume=` hint。任意 UUID は使わない。読めなければ空）
- `exitCode`
- `outputText`
- `outputKind`（`final_response` または `assistant_transcript`）
- `runId`（`jobId` と同じ）
- `eventsLogPath`
- `textLogPath`

この compact な `result` は `start_agent` の idempotent retry、`wait_agent_job`、`cancel_agent_job` でも同じである。

Cursor は terminal `result.result` とassistantイベントの対応を確認できる場合だけ、最後のassistant報告を `final_response` として返す。対応を確認できないterminal resultや、result自体が欠落する形式では、情報を削らず `assistant_transcript` として返す。Copilot は対になった `assistant.turn_start` / `assistant.turn_end` とterminal `result`を確認できる場合だけ、最後に完了したassistant turnを `final_response` とする。未完了turn、他種イベントのcompleted属性、対応 lifecycle が確認できない旧形式では、認識済みassistant本文の連結を `assistant_transcript` とする。過去 turn、reasoning、tool の前後の断片を根拠なく削除したり、暗黙に要約・truncateしたりしない。

`failed` / `cancelled` のsnapshotは `error` を返す。`failed` には可能な場合、`failure.kind`（`process_start_failed` / `non_zero_exit` / `output_parse_failed` / `facade_interrupted` / `internal_error`）、単一行で redaction 済みの最大 512 文字の `summary`、取得できた `exitCode`、生成済み run log の参照を含める。例外全文、stdout/stderr 全文、raw stream は公開しない。不明なjob IDや不正な引数などtool呼び出し自体の失敗だけをMCP tool errorとし、workerの失敗を成功扱いや別jobへのフォールバックへ変換しない。

### `wait_agent_job`

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `job_id` | はい | `start_agent` が返した `jobId` |
| `timeout_seconds` | いいえ | terminal まで待つ上限秒。省略時は `300`。明示時の範囲は 1〜86400 |

通常の完了待ちは `wait_agent_job(job_id)` とし、`timeout_seconds` を省略する。診断・テスト・上位環境の明示的な制約など、既定値を上書きする理由がある場合だけ指定する。新しい worker を開始・再実行しない。既に terminal なら即時に返す。running なら terminal 化または待機上限まで待つ。timeout 時は `status=running` の snapshot を返し、worker は継続する。wait 呼び出しのキャンセル・切断・timeout だけでは worker を cancel しない。worker 停止は `cancel_agent_job` の責務である。

`completed` は CLI 実行が完了したことだけを示す。親エージェントは応答、差分、テスト、run log などをレビューしてから受入判断を行う。`failed`、応答喪失、wait timeout は別状態として扱い、応答喪失の回収 retry は同じ `request_id` を使って既存 job を取得する。

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

Codex UI へのストリーミング表示とは独立して、各 agent job の逐次出力を Facade 専用ディレクトリへ保存する。対象リポジトリの working tree は使わない。run log は観測用であり、`wait_agent_job` / `get_agent_job` の代わりにはならない。raw な逐次出力はここから確認する。

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

`runId` は `jobId` と同じ値である。パスは completed の `result` に含まれる。heartbeat は 15 秒間隔で、経過時間・process 生存・最後の外部出力からの経過を記録する。出力が無いこととハングは同義ではない。認証情報・credential・token は書き込み前に `[REDACTED]` へ置換する。起動時には PATH 解決後の実行ファイル、wrapper 種別、host / wrapper switch と論理引数を記録する。

## 親エージェント用 Skill のグローバル導入

Codex が計画・分割・レビュー・受入を担当し、外部 agent へ実作業を委譲する場合は、親用 Skill をユーザースコープへ導入する。

```powershell
apm install -g suusanex/codex_agent_facade/apm-packages/external-agent-orchestration --target codex,agent-skills
```

Codex で `$external-agent-orchestration` と「このスレッドでは github-copilot へ委譲してください」を指定する。Cursor なら委譲先を `cursor` にする。モデルは変更しない。MCP 接続は別途必要である。

既存の中継用 Skill とは責任が異なるため、同じ作業に重ねて適用しない。導入・更新・使い分けは [親用 Skill の README](apm-packages/external-agent-orchestration/README.md) を参照。

## 編集対象リポジトリへの Skill 導入

Skill は **編集する work repository** の root で APM から入れる。この Facade リポジトリへ入れる必要はない。MCP server は APM では入らない。

```powershell
apm install suusanex/codex_agent_facade/apm-packages/github-copilot --target codex,agent-skills
apm install suusanex/codex_agent_facade/apm-packages/grok-build --target codex,agent-skills
apm install suusanex/codex_agent_facade/apm-packages/cursor --target codex,agent-skills
```

ローカル checkout から入れる場合:

```powershell
apm install "C:\path\to\codex_agent_facade\apm-packages\github-copilot" --target codex,agent-skills
apm install "C:\path\to\codex_agent_facade\apm-packages\grok-build" --target codex,agent-skills
apm install "C:\path\to\codex_agent_facade\apm-packages\cursor" --target codex,agent-skills
```

展開先は `.agents/skills/github-copilot/`、`.agents/skills/grok-build/`、`.agents/skills/cursor/`。Codex 上では `$github-copilot` / `$grok-build` / `$cursor` で本文を外部 agent へ渡す。これらの Skill を指定した turn では、その Skill の契約どおり Codex 自身は対象作業を実行せず、Skill より後のユーザー本文を worker prompt として外部 agent へ委譲し、結果を中継する。Skill 無しで `start_agent` / `wait_agent_job` を直接呼ぶ場合、呼び出し側は元の user prompt 全体を転送する必要はなく、限定した worker 専用 prompt を構成して渡してよい。`get_agent_job` は明示照会・復旧・診断用である。

更新・削除:

```powershell
apm update
apm uninstall github-copilot
apm uninstall grok-build
apm uninstall cursor
```

## CLI 変換

GitHub Copilot（プロセス cwd = `working_directory`）:

```text
<UTF-8 prompt source> | copilot --output-format json [--allow-all] [--resume <session_id>]
```

Grok Build:

```text
grok --no-auto-update -p <prompt> --cwd <working_directory> --output-format streaming-json [--always-approve] [--resume <session_id>]
```

Cursor CLI（プロセス cwd = `working_directory`。実行ファイル名は Unix では `cursor-agent`、Windows では `cursor-agent.ps1`）:

```text
cursor-agent --print --output-format stream-json --trust --workspace <working_directory> [--force] [--resume <session_id>] <prompt>
```

`--allow-all` / `--always-approve` / `--force` は `auto_approve=true` のときだけ付ける。Copilot は全OSで PATH 上の `copilot` を選び、`--prompt` は使わず、Skill付き完全promptをUTF-8 stdinへ渡す。GitHub公式の [programmatic usage](https://docs.github.com/en/copilot/how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically) に従う。Windowsの`copilot.CMD`は汎用cmd経路でstdin handleをchildへ継承し、PATH上で`copilot.exe`が先に解決される環境では通常のnative経路を使う。npm shim内容やnpm loaderの解析は行わない。Cursor は PATH 上の `cursor-agent` を使う。同梱の `agent` は Grok Build の `agent` と衝突するため使わない。Windows では公式の `cursor-agent.ps1` を `pwsh -File` で起動する。`cursor-agent.cmd` は cmd が CR/LF を引数へ渡せないため使わない。

Skill 変換は共通化しない。Copilot は `Use the /name skill.`、Grok は `/name` 行。Cursor は Codex / `.codex/skills` を native discovery するため、`skills` 配列を prompt へ変換しない。prompt 本文で `/skill-name` と書けば headless でも invoke できる。詳細は `docs/poc-observations.md`。

## テスト

CI / 通常テストは実 `copilot` / `grok` / `cursor-agent` を呼ばない（`dotnet --version` の収集確認だけ実プロセスを使う）。
Windows の `.cmd` / `.bat` は `ProcessStartInfo.Arguments` の raw command string として `cmd.exe /d /v:off /s /c` で起動する。`.NET` の `ArgumentList` は使わず、引用符は二重化し、`%` はプロセス限定環境変数の置換結果で保護してから cmd に渡す。`&`、`|`、`^`、空白、日本語、`!`、括弧、`<`、`>`、引用符を含む値は実プロセス fixture で検証している。NUL と CR/LF は cmd のバッチ引数 ABI で忠実かつ安全に表現できないため、`.cmd` / `.bat` 経路では実行前エラーになる。Copilotの複数行promptは公式stdin経路で渡し、`--prompt`と併用しない。stdin指定時はUTF-8 BOMなしで本文をそのままwrite/flush/closeし、launch logには本文を記録せず、指定有無とbyte countだけを記録する。通常の`.ps1`は汎用`pwsh.exe -NoLogo -NoProfile -NonInteractive -File <script>`の`ArgumentList`、通常の`.exe`は従来どおり`ArgumentList`を使う。stdout は UTF-8 JSONL のまま、Windows の `.cmd` / `.bat` wrapper の stderr は OS の OEM encoding、wrapperなし（native executable と PowerShell host）は UTF-8として厳密にデコードする。選択した encoding で解釈できなければ実行を失敗させる。

```powershell
dotnet run --file tests/CodexAgentFacade.Tests.cs
```

実 CLI 観測用ハーネス（公開 MCP I/F ではない）:

```powershell
dotnet run --file src/PocSmoke.cs
```

### Cursor CLI の事前セットアップ

Windows:

```powershell
# PowerShell 7 が未導入の場合
winget install --id Microsoft.PowerShell --source winget
pwsh --version

irm 'https://cursor.com/install?win32=true' | iex
cursor-agent --version
cursor-agent login
cursor-agent status
```

macOS / Linux:

```bash
curl https://cursor.com/install -fsS | bash
cursor-agent --version
cursor-agent login
```

認証は `cursor-agent login`（ブラウザ）または環境変数 `CURSOR_API_KEY`。Facade は API key を引数へ渡さない。Windows では Cursor CLI の `.ps1` launcher を実行するため、PowerShell 7 の `pwsh.exe` が必要になる。

この環境では Grok Build が `agent` を PATH に置く。Cursor の installer も `agent` を作るが、Facade は衝突を避けるため `cursor-agent` だけを起動する。

Cursor 固有の対応:

| Facade | Cursor CLI |
| --- | --- |
| 非対話 | `--print` |
| streaming | `--output-format stream-json`（`--stream-partial-output` は付けない） |
| working directory | `--workspace` と process cwd |
| `session_id` 継続 | `--resume <session_id>`。最新 session を取る `--continue` は使わない |
| 新規 `sessionId` | stream-json の明示フィールド `session_id` だけを返す。`request_id` や任意 UUID は使わない |
| `auto_approve=true` | `--force`（コマンド / ファイル変更の承認を省略。denied なものは通さない） |
| `auto_approve=false` | `--force` を付けない。workspace 信頼ダイアログだけ `--trust` で避ける |
| Skills | 変換しない。Cursor は `.codex/skills` 等を native discovery する。明示 invoke は prompt の `/skill-name` |

`--force` は Copilot の `--allow-all` や Grok の `--always-approve` と完全同義ではない。Cursor の permission model では「明示 deny 以外を通す」フラグであり、MCP server 承認（`--approve-mcps`）や sandbox は別スイッチである。Facade はそれらを勝手に付けない。

`--trust` は `auto_approve` とは独立で、headless 実行が未信頼 workspace の確認で止まらないように常に付ける。

print モードでは公式ドキュメント上 `thinking` event は出ない。出た場合は run log の thought として残し、`outputText` には混ぜない。最終応答は `type=result` の `result` を優先する。このフィールドは assistant 本文の連結であり、tool 前の中間 assistant 文も含む。`request_id` は session ID ではない。

## 観測

PoC の成果物は実装に加え、成立 / 不可の記録である。`docs/poc-observations.md` を更新する。

## 人手での作業が必要

- `CODEX_AGENT_FACADE_TOKEN` をユーザー環境に設定し、Facade プロセスを事前起動する（Windows では Task Scheduler から `CodexAgentFacade.exe` を直接起動してよい）
- 常駐 exe を直接起動したときコンソールウィンドウが出ないことの確認（WinExe。自動テストでは検証しない）
- GitHub Copilot CLI と Grok Build CLI、Cursor CLI へのログイン
- Codex への MCP 登録（`url`、`bearer_token_env_var`、`enabled = true`、`direct_only_tool_namespaces`）
- Facade 再起動後に自動 reconnect しない場合の、同一 thread 上での MCP refresh / reconnect
- Desktop Codex App の composer / 完了通知 / 別 thread 並行（HTTP 移行後の start/get 実機確認は `docs/poc-observations.md`）
