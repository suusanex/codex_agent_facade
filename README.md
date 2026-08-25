# codex_agent_facade

Codex App を薄い UI shell として、GitHub Copilot と Grok Build へ作業を中継する feasibility PoC。

Codex / Facade は planner や orchestrator にならない。ユーザーの prompt を構造化 MCP 入力として受け、選択した agent の CLI へ変換して実行し、応答を同じ Codex thread へ返す。

## 必要環境

- .NET 11 SDK（Preview 可）。`#:include` で複数ファイルをコンパイルする
- PATH 上の `copilot`（GitHub Copilot CLI）および / または `grok`（Grok Build CLI）
- 実作業には各 CLI へのログインが必要

このリポジトリは File-based apps を使う。`.csproj` は無い。

## 起動

```powershell
dotnet run --file src/CodexAgentFacade.cs
```

stdio MCP server として起動する。ログは stderr へ出す。

ビルド確認だけする場合:

```powershell
dotnet publish src/CodexAgentFacade.cs
```

## Codex への接続

`~/.codex/config.toml` または信頼済みプロジェクトの `.codex/config.toml` に追加する。

別リポジトリを編集するときは、編集対象ではなく **ユーザー設定** `~/.codex/config.toml` に書く。

`/mcp` ではこの server を有効化できない。設定ファイルに最初から `enabled = true` を書く。

`run_agent` は長時間の同期実行になる。`direct_only_tool_namespaces` を指定しないと、Codex 側が進捗確認とタイムアウトを行い、期待どおり完了しない。`tool_timeout_sec` の既定は 60 秒なので、agent 実行向けに延長する。いずれもホスト側設定であり、Facade の迂回実装ではない。

実機で確認した設定（`command` / `cwd` は配置先に置き換える）:

```toml
[features.code_mode]
direct_only_tool_namespaces = ["mcp__codex_agent_facade"]

[mcp_servers.codex_agent_facade]
command = "C:/path/to/CodexAgentFacade/CodexAgentFacade.exe"
args = []
cwd = "C:/path/to/CodexAgentFacade"
startup_timeout_sec = 120
tool_timeout_sec = 1800
default_tools_approval_mode = "auto"
enabled = true
```

`command` は `dotnet run --file` でも、`dotnet publish` した `CodexAgentFacade.exe` でもよい。exe を使う場合は成果物フォルダごと配置し、ソースツリーは不要。

開発時に `dotnet run --file` で起動する例（`[features.code_mode]` は同じ）:

```toml
[mcp_servers.codex_agent_facade]
command = "dotnet"
args = ["run", "--file", "C:/path/to/codex_agent_facade/src/CodexAgentFacade.cs"]
cwd = "C:/path/to/codex_agent_facade"
startup_timeout_sec = 120
tool_timeout_sec = 1800
default_tools_approval_mode = "auto"
enabled = true
```

Codex App では設定を保存したあと Restart する。

## MCP tool

公開 tool は `run_agent` のみ。

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `agent` | はい | `github-copilot` または `grok-build` |
| `prompt` | はい | 対象 agent へ渡す本文。Facade は再構成しない |
| `working_directory` | はい | 対象 workspace / worktree |
| `session_id` | いいえ | 同一外部 session の継続。省略時は新規 |
| `skills` | いいえ | Codex 形式の Skill 名。Driver ごとに native 形式へ変換する |
| `auto_approve` | いいえ | 既定 true。各 CLI の non-interactive 承認フラグを付ける。質問待ちの観測では false |

戻り JSON:

- `agent`
- `sessionId`（CLI が明示した session フィールド、または Copilot の `--resume=` hint。任意 UUID は使わない。読めなければ空）
- `exitCode`
- `outputText`
- `rawOutput`

失敗時はフォールバックせず MCP tool error になる。

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

展開先は `.agents/skills/github-copilot/` と `.agents/skills/grok-build/`。Codex 上では `$github-copilot` / `$grok-build` で本文を外部 agent へ渡す。Skill 無しで `run_agent` を直接呼んでもよい。

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
grok --no-auto-update -p <prompt> --cwd <working_directory> --output-format json [--always-approve] [--resume <session_id>]
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

- GitHub Copilot CLI と Grok Build CLI へのログイン
- Codex への MCP 登録（`enabled = true`、`direct_only_tool_namespaces`、`tool_timeout_sec` 延長）
- Desktop Codex App の composer / 完了通知 / 別 thread 並行（Codex CLI 0.149.0 からの MCP `run_agent` 往復は確認済み）
