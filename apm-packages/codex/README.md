# Codex CLI Skill

`$codex` は、Codex 自身は対象作業を実行せず、Codex CLIへ作業 payload を委譲して結果を中継する Skill だ。Codex 自身への作業実行指示ではない。MCP server とユーザーの Codex MCP 設定は別途必要。

## Install

```powershell
apm install suusanex/codex_agent_facade/apm-packages/codex --target codex,agent-skills
```

展開先は `.agents/skills/codex/SKILL.md`。

## Use

作業本文の前に `facade-options` blockを置くときは `model`, `reasoning_effort`, `fast`, `auto_approve` を指定できる。未指定optionはCLI既定を使い、`fast: false` は明示的にdefault tierを要求する。prompt本文に現れたモデル名から起動設定を推測しない。本文中のモデル名は起動設定にしない。`$codex` は親の委譲ヘッダであり、worker prompt にも `skills` にも入れない。`/codex` を prompt へ追加しない。実行オプションが無いときは委譲ヘッダより後ろの本文が作業 prompt である。`facade-options` の `key: value` は `model`, `reasoning_effort`, `fast`, `auto_approve` である。`auto_approve` は `true` または `false` で、未指定時は引数を渡さない。`skills` は未知キーとしてエラーにする。presence flag の `plan` はこの agent では未対応なので `start_agent` を呼ばない。`fast` フラグは在るときだけ true で、無いときは送らない。この relay は新規 job と継続では `skills` を渡さない。

```text
$codex このリポジトリで対象変更を実装し、結果を報告して。
```

```facade-options
model: gpt-6-luna
reasoning_effort: low
fast: false
```
このリポジトリを読み取り専用で調べ、結果を簡潔に返して。

`agent` は `codex` を指定する。各新規jobに新しい `request_id` を使う。exact retryは全引数と同じIDを再利用し、完了後の継続は新しいIDと前回 `result.sessionId` を `session_id` に使う。required fields を毎回指定する。`auto_approve: false` は `read-only` sandboxと `approval_policy=never` を選び、承認質問を表示しない。

## Validation

リポジトリ全体のテスト:

```powershell
dotnet run --file tests/CodexAgentFacade.Tests.cs
```

ローカルpackageを一時ディレクトリへ導入し、Codex SkillのSHA-256一致とFacade binaryを含まないことを確認する:

```powershell
dotnet run --file apm-packages/codex/scripts/test-apm-package-install.cs
```
