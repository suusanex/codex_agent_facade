# Jules Skill

`$jules` は、Codex 自身は対象作業を実行せず、Skill より後のユーザー本文を Codex Agent Facade の MCP job host 経由で Jules へ委譲し、その結果を中継する Skill です。Codex は Jules と協力して作業するのではなく、薄い UI shell / relay として Facade の結果を返します。

MCP server 本体はこの package に含まれません。Facade の publish 成果物と `~/.codex/config.toml` は OS user 単位で別に設定します。必須項目（`url`、`bearer_token_env_var`、`enabled = true`、`direct_only_tool_namespaces` など）はリポジトリ root の README「Codex への接続」を参照してください。Jules は単発 CLI では起動しない。API key は Facade プロセスの環境変数 `JULES_API_KEY` だけを使う。

## Install

導入先は **編集する work repository** の root です。この Facade リポジトリ自身へ入れる必要はありません。

GitHub から:

```powershell
apm install suusanex/codex_agent_facade/apm-packages/jules --target codex,agent-skills
```

ローカル checkout から:

```powershell
apm install "C:\path\to\codex_agent_facade\apm-packages\jules" --target codex,agent-skills
```

展開先は `.agents/skills/jules/SKILL.md` です。

## Use

Codex 上で対象リポジトリを開いた状態で、Skill を指定して作業本文を書く。Skill より後の本文は Jules へ渡す作業 payload であり、Codex 自身への作業実行指示ではない。`working_directory` は今開いているリポジトリの絶対パスにする。required fields を毎回指定する。新しい依頼、回答、継続では新しい `request_id` を生成する。新規 Jules session では `session_id` を渡さない。`waiting_for_input` への回答、または同じ Jules session の明示継続のときだけ、直前の `sessionId` を `session_id` に渡す。同じ Codex thread だけでは過去の session を再利用しない。

`$jules` は親の委譲ヘッダであり、worker prompt にも `skills` にも入れない。`/jules` を prompt へ追加しない。`facade-options` で送ってよいのは `auto_approve: true` だけである。未指定時は引数を渡さず Facade 既定（true）を使う。`model`、`reasoning_effort`、`fast`、`plan`、`skills`、`auto_approve: false` は非対応なので、黙って省略せず `start_agent` を呼ばない。この relay は新規 job と継続では `skills` を渡さない。本文中のモデル名は起動設定にしない。

Facade が clean worktree、upstream、remote branch tip、Jules Source との対応を拒否したら、その内容を報告する。条件を満たすための commit、push、fetch、rebase、checkout はしない。

`waiting_for_input` はタスク完了ではない。`inputPrompt` と、あるときは `sessionUrl` と `artifacts` を中継し、回答はユーザーの次の `$jules` 本文だけを新しい job で送る。`remotePhase=submission_unknown` または `communication_error` のあいだは同じ `jobId` を再照会し、別 request で再送しない。`cancel_agent_job` は unsupported であり、cancelled になったとは報告しない。patch は自動適用しない。

```text
$jules このリポジトリの README に使い方を追記して。
```

Skill 無しで MCP tool `start_agent` / `wait_agent_job` を直接呼んでもよい。`agent` は `jules` にする。`get_agent_job` は明示照会、復旧、診断、受理不明の再照会に使う。

## Update and remove

```powershell
apm update
apm uninstall jules
```

APM の更新・削除は Facade 実行ファイルとユーザーの MCP 設定を変更しません。

## Validation

```powershell
pwsh -NoProfile -File apm-packages/jules/scripts/test-apm-package-install.ps1
```
