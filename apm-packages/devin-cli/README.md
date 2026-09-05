# Devin CLI Skill

`$devin-cli` は、Codex 自身は対象作業を実行せず、Skill より後のユーザー本文を Codex Agent Facade 経由で Devin CLI へ委譲し、その結果を中継する Skill です。Codex は Devin CLI と協力して作業するのではなく、薄い UI shell / relay として Facade の結果を返します。

MCP server 本体はこの package に含まれません。Facade の publish 成果物と `~/.codex/config.toml` は OS user 単位で別に設定します。必須項目（`url`、`bearer_token_env_var`、`enabled = true`、`direct_only_tool_namespaces` など）はリポジトリ root の README「Codex への接続」を参照してください。

## Install

導入先は **編集する work repository** の root です。この Facade リポジトリ自身へ入れる必要はありません。

GitHub から:

```powershell
apm install suusanex/codex_agent_facade/apm-packages/devin-cli --target codex,agent-skills
```

ローカル checkout から:

```powershell
apm install "C:\path\to\codex_agent_facade\apm-packages\devin-cli" --target codex,agent-skills
```

展開先は `.agents/skills/devin-cli/SKILL.md` です。

## Use

Codex 上で対象リポジトリを開いた状態で、Skill を指定して作業本文を書く。Skill より後の本文は Devin CLI へ渡す作業 payload であり、Codex 自身への作業実行指示ではない。`working_directory` は今開いているリポジトリのパスにする。

```text
$devin-cli このリポジトリの README に使い方を追記して。
```

Skill 無しで MCP tool `start_agent` / `get_agent_job` を直接呼んでもよい。

## Update and remove

```powershell
apm update
apm uninstall devin-cli
```

APM の更新・削除は Facade 実行ファイルとユーザーの MCP 設定を変更しません。

## Validation

```powershell
pwsh -NoProfile -File apm-packages/devin-cli/scripts/test-apm-package-install.ps1
```
