# PoC 観測ログ

issue [#1](https://github.com/suusanex/codex_agent_facade/issues/1) の 8 項目。成立させること自体は成功条件ではない。迂回実装で不可を隠さない。

状態の意味:

- `成立` — 実機で確認できた
- `部分成立` — 経路の一部だけ確認できた
- `不可` — 現行 CLI / Codex / MCP の組み合わせでは成立しない
- `人手確認待ち` — 実装はあるが実 Codex App / 実 CLI での確認が残っている
- `実装上の境界` — コードから読み取れる責務分割。実機確認とは独立

| # | 観測項目 | 状態 | 根拠 |
| --- | --- | --- | --- |
| 1 | Codex 上で選択した agent へ実作業 prompt を渡し、同じ thread へ返せるか | 人手確認待ち | MCP tool `run_agent` が prompt を Driver へ渡し、CLI 最終出力を JSON で返す。Codex App 上の往復は未確認 |
| 2 | Codex 形式の Skill を GitHub Copilot / Grok Build へ実用的に変換できるか | 人手確認待ち | Driver は `$name` / `name` を各 agent の native `/name` 行として prompt 先頭へ付与する。cwd 上の skill 発見は各 CLI に任せる。実機での skill 実行は未確認 |
| 3 | 同じ外部 session へ次のユーザー入力を継続できるか | 人手確認待ち | Copilot は `--resume <id>`、Grok は `--session-id <id>`。session id は CLI 出力から読む。取れなければ空文字。`--prompt` と `--resume` の同時指定は未確認 |
| 4 | 質問・permission を Codex の自然な入力待ちへ橋渡しできるか | 人手確認待ち | 独自 queue は無い。正常系は `--allow-all` / `--always-approve`。質問観測はこれらを外した別試行が必要 |
| 5 | streaming output を Codex UI へ逐次表示できるか | 人手確認待ち | stdout 各行を MCP progress notification に載せる。既定 CLI 形式は json（完了時 object / JSONL）。Codex UI が表示するかは未確認。独自 stream プロトコルは無い |
| 6 | 数分の実行中に別 Codex thread / worktree を使えるか | 人手確認待ち | Facade は同期 MCP tool 呼び出し。Codex 既定 `tool_timeout_sec` は 60 秒のため、設定で延長が必要。並行利用は Codex 側の挙動 |
| 7 | 完了が Codex 本来の通知・thread 一覧・復帰と連動するか | 人手確認待ち | Facade は MCP tool 完了以上の通知を送らない。連動するなら Codex の tool 完了に乗る |
| 8 | 2 Driver 間で何が Facade 共通で、何が agent 固有か | 実装上の境界 | 下記 |

## 8. 共通責務と agent 固有責務

Facade 共通:

- MCP tool `run_agent` の schema と入力バリデーション（agent / prompt / working_directory）
- agent 名による Driver 選択
- プロセス起動（cwd、引数配列、stdout/stderr 収集、cancel 時の process tree kill、例外の `ToString()` トレース）
- Codex `$skill` を slash command 行へ正規化する処理（両 CLI が `/name` を native とする範囲）

GitHub Copilot 固有:

- 実行ファイル `copilot`
- `--prompt` / `--output-format json` / `--allow-all` / `--resume`
- 作業ディレクトリはプロセス cwd のみ（CLI の `--cwd` は使わない）
- JSONL 1 行 1 object の解釈、`--resume=<uuid>` hint からの session id 抽出

Grok Build 固有:

- 実行ファイル `grok`
- `--no-auto-update` / `-p` / `--cwd` / `--output-format json` / `--always-approve` / `--session-id`
- 末尾 JSON object の解釈（先行ログがある場合は最後の `{` から）

意図的に共通化していないもの:

- CLI フラグ、permission モデル、session フラグの意味、JSON スキーマ、ACP
