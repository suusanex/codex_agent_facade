# 外部エージェントの統括 Skill

`$external-agent-orchestration` は、Codex の親エージェントが要件理解・設計判断・作業分解・レビュー・受入を担当し、調査・実装・テスト・修正を Codex Agent Facade 経由で外部 worker へ委譲する Skill です。Astra / Sol のどちらでも利用でき、モデル自体は変更しません。

## グローバル導入

`--global` に対応した APM を使用し、任意のディレクトリで実行します。

```powershell
apm install -g suusanex/codex_agent_facade/apm-packages/external-agent-orchestration --target codex,agent-skills
```

ローカル checkout から導入する場合:

```powershell
apm install -g "C:\path\to\codex_agent_facade\apm-packages\external-agent-orchestration" --target codex,agent-skills
```

APM のユーザースコープで管理するため、編集対象リポジトリごとの導入は不要です。対象リポジトリだけへ導入したい場合は、その root で `-g` を省いて実行します。

MCP server 本体や各 CLI はこのパッケージに含まれません。[リポジトリの接続手順](../../README.md#codex-への接続)に従って Facade を起動・登録し、使用する外部 CLI の認証を済ませてください。

## 使い方

Codex で編集対象リポジトリを開き、スレッド冒頭で Skill と委譲先を指定します。

```text
$external-agent-orchestration
このスレッドでは github-copilot を外部 worker として使用してください。
対象 issue の内容と現状を調べ、プランを作成してください。実装はまだ行わないでください。
```

Cursor を使う場合は委譲先を `cursor` に変更します。未指定なら Skill は委譲先を確認します。「このスレッド」と指定した場合は、変更指示まで役割と委譲先を引き継ぎます。

親は必要な根拠を直接確認しますが、成果物の編集・修正は worker へ戻します。Facade が使えない場合は、親実装や別 agent への切替で代替せず、阻害要因を報告します。「調査のみ」「プランまで」という依頼は、その範囲のまま委譲します。

## 既存 Skill との使い分け

| Skill | Codex の役割 |
| --- | --- |
| `external-agent-orchestration` | 作業を分解して委譲し、成果をレビューして受入判断を行う |
| `github-copilot` / `cursor` / `grok-build` | ユーザー本文をそのまま外部 agent へ渡し、結果を中継する |

この親用 Skill は中継用 Skill に依存しません。同じ作業で両者の役割を重ねず、MCP tool を直接使用します。引数、待機、再試行、session 継続は MCP の公開説明を正とし、Skill 側へ固定値を複製しません。

## 更新・削除

GitHub からグローバル導入した場合:

```powershell
apm install -g suusanex/codex_agent_facade/apm-packages/external-agent-orchestration --target codex,agent-skills --update
apm uninstall -g suusanex/codex_agent_facade/apm-packages/external-agent-orchestration
```

ローカル導入した場合は、導入時のローカルパスを指定します。APM の操作は Facade 本体やユーザーの MCP 設定を変更しません。

APM の仕様: [install](https://microsoft.github.io/apm/reference/cli/install/) / [uninstall](https://microsoft.github.io/apm/reference/cli/uninstall/)
