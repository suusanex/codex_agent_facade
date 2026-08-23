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

`tool_timeout_sec` の既定は 60 秒なので、agent 実行向けに延長する。これはホスト側設定であり、Facade の迂回実装ではない。

```toml
[mcp_servers.codex_agent_facade]
command = "dotnet"
args = ["run", "--file", "C:/path/to/codex_agent_facade/src/CodexAgentFacade.cs"]
startup_timeout_sec = 60
tool_timeout_sec = 1800
```

`C:/path/to/codex_agent_facade` はクローンしたパスに置き換える。Codex App では MCP server を保存したあと Restart する。

## MCP tool

公開 tool は `run_agent` のみ。

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `agent` | はい | `github-copilot` または `grok-build` |
| `prompt` | はい | 対象 agent へ渡す本文。Facade は再構成しない |
| `working_directory` | はい | 対象 workspace / worktree |
| `session_id` | いいえ | 同一外部 session の継続。省略時は新規 |
| `skills` | いいえ | Codex 形式の Skill 名。各 Driver が `/name` へ変換する |

戻り JSON:

- `agent`
- `sessionId`（CLI 出力から読めた場合。読めなければ空）
- `exitCode`
- `outputText`
- `rawOutput`

失敗時はフォールバックせず MCP tool error になる。

## Codex Skill 入口（任意）

UX は固定しない。入口の例は `examples/codex-skills/` にある。

- `examples/codex-skills/github-copilot/SKILL.md`
- `examples/codex-skills/grok-build/SKILL.md`

使う場合は、Codex が読む skills ディレクトリへコピーする。Skill 無しで `run_agent` を直接呼んでもよい。

## CLI 変換

GitHub Copilot（プロセス cwd = `working_directory`）:

```text
copilot --prompt <prompt> --output-format json --allow-all [--resume <session_id>]
```

Grok Build:

```text
grok --no-auto-update -p <prompt> --cwd <working_directory> --output-format json --always-approve [--session-id <session_id>]
```

`--allow-all` / `--always-approve` は各 CLI の non-interactive 公式フラグ。質問・permission の橋渡しを観測するときは、これらのフラグを外した別試行が必要。

## テスト

実 `copilot` / `grok` は呼ばない（`dotnet --version` の収集確認だけ実プロセスを使う）。

```powershell
dotnet run --file tests/CodexAgentFacade.Tests.cs
```

## 観測

PoC の成果物は実装に加え、成立 / 不可の記録である。`docs/poc-observations.md` を更新する。

## 人手での作業が必要

- GitHub Copilot CLI と Grok Build CLI へのログイン
- Codex への MCP 登録と `tool_timeout_sec` 延長
- `docs/poc-observations.md` の実機項目（往復、session 継続、質問、streaming、並行利用、完了通知）
