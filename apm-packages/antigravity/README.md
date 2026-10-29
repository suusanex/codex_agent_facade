# Antigravity Skill

`$antigravity` は、Codex 自身は対象作業を実行せず、Skill より後のユーザー本文を Codex Agent Facade 経由で Antigravity CLI へ委譲し、その結果を中継する Skill です。Codex は Antigravity と協力して作業するのではなく、薄い UI shell / relay として Facade の結果を返します。

MCP server 本体はこの package に含まれません。Facade の publish 成果物と `~/.codex/config.toml` は OS user 単位で別に設定します。必須項目（`url`、`bearer_token_env_var`、`enabled = true`、`direct_only_tool_namespaces` など）はリポジトリ root の README「Codex への接続」を参照してください。PATH 上の `agy` が必要です。

## Install

導入先は **編集する work repository** の root です。この Facade リポジトリ自身へ入れる必要はありません。

GitHub から:

```powershell
apm install suusanex/codex_agent_facade/apm-packages/antigravity --target codex,agent-skills
```

ローカル checkout から:

```powershell
apm install "C:\path\to\codex_agent_facade\apm-packages\antigravity" --target codex,agent-skills
```

展開先は `.agents/skills/antigravity/SKILL.md` です。

## Use

Codex 上で対象リポジトリを開いた状態で、Skill を指定して作業本文を書く。Skill より後の本文は Antigravity へ渡す作業 payload であり、Codex 自身への作業実行指示ではない。`working_directory` は今開いているリポジトリの絶対パスにする。required fields を毎回指定する。新しい依頼では新しい `request_id` を生成する。同じ Antigravity conversation を続けるときだけ、直前の対応する result の `sessionId` を `session_id` に渡す。同じ Codex thread だけでは `session_id` を渡さない。

`$antigravity` は親の委譲ヘッダであり、worker prompt にも `skills` にも入れない。`/antigravity` を prompt へ追加しない。`facade-options` の `key: value` は `model`, `reasoning_effort`, `auto_approve` を渡せる。`auto_approve` は `true` または `false` で、未指定時は引数を渡さず Facade 既定（true）を使う。`true` は `--dangerously-skip-permissions` を付ける。`false` は headless の対話承認ではなく、tool が soft-deny され得る。`skills` は未知キーとしてエラーにする。presence flag の `plan` があるときだけ `run_mode=plan` を渡す。無いときは渡さず、前回 turn から継承しない。成功した plan の `outputKind` は `plan` で、`outputText` をそのまま中継する。`planPath` は付かない。`fast` はこの agent では未対応なので、黙って省略せず `start_agent` を呼ばない。この relay は新規 job と継続では `skills` を渡さない。`model` は Antigravity の model slug、`reasoning_effort` は effort token で、どちらも変換しない。本文中のモデル名は起動設定にしない。

`completed` でも `completionReason` が `permission_soft_denied` のときは完全成功と言い換えない。`WAITING` / `RUNNING` は Facade の `status` に従い、成功へ読み替えない。

```text
$antigravity このリポジトリの README に使い方を追記して。
```

実行オプションは作業本文の前にある先頭 `facade-options` blockで指定する。

    ```facade-options
    model: <agy-model-slug>
    reasoning_effort: high
    auto_approve: false
    ```
    このリポジトリの README に使い方を追記して。

識別子は [Agent モデル識別子一覧](../../docs/agent-model-ids.md) を参照する。Skill 無しで MCP tool `start_agent` / `wait_agent_job` を直接呼んでもよい。`get_agent_job` は明示照会・復旧・診断用である。

## Update and remove

```powershell
apm update
apm uninstall antigravity
```

APM の更新・削除は Facade 実行ファイルとユーザーの MCP 設定を変更しません。

## Validation

```powershell
pwsh -NoProfile -File apm-packages/antigravity/scripts/test-apm-package-install.ps1
```
