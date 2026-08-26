# GitHub Copilot Skill

`$github-copilot` は、Codex thread の本文を Codex Agent Facade の MCP tool `run_agent` 経由で GitHub Copilot CLI へ渡す Skill です。

MCP server 本体はこの package に含まれません。Facade の publish 成果物と `~/.codex/config.toml` は OS user 単位で別に設定します。必須項目（`url`、`bearer_token_env_var`、`enabled = true`、`direct_only_tool_namespaces` など）はリポジトリ root の README「Codex への接続」を参照してください。

## Install

導入先は **編集する work repository** の root です。この Facade リポジトリ自身へ入れる必要はありません。

GitHub から:

```powershell
apm install suusanex/codex_agent_facade/apm-packages/github-copilot --target codex,agent-skills
```

ローカル checkout から:

```powershell
apm install "C:\path\to\codex_agent_facade\apm-packages\github-copilot" --target codex,agent-skills
```

展開先は `.agents/skills/github-copilot/SKILL.md` です。

## Use

Codex 上で対象リポジトリを開いた状態で、Skill を指定して作業本文を書く。`working_directory` は今開いているリポジトリのパスにする。

```text
$github-copilot このリポジトリの README に使い方を追記して。
```

Skill 無しで MCP tool `run_agent` を直接呼んでもよい。

## Update and remove

```powershell
apm update
apm uninstall github-copilot
```

APM の更新・削除は Facade 実行ファイルとユーザーの MCP 設定を変更しません。

## Validation

```powershell
pwsh -NoProfile -File apm-packages/github-copilot/scripts/test-apm-package-install.ps1
```
