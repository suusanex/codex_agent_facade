# Agent モデル識別子一覧

この一覧は、relay SKILL の `facade-options` ブロック内 `model:` 行、および MCP `start_agent` の `model` 引数へ渡す **CLI 識別子** を記録する。Codex App / Cursor IDE / GitHub Copilot App など UI 上のモデル名とは文字列が一致しない場合がある。Facade は、Cursor で `reasoning_effort` または `fast` を指定したときを除き、識別子を変換せず指定時だけ各 CLI の `--model` へそのまま渡す。Cursor の選択規則は下の effort / fast 表を参照する。

**スナップショット取得日:** 2026-11-23。各一覧は取得時の認証アカウント、プラン、接続設定に基づく。Jules に model 識別子は無い。

## 使い方

relay SKILL 利用時:

```facade-options
model: composer-2.5-fast
```
作業 prompt

MCP 直接呼び出し時は `start_agent` の `model` 引数へ同じ識別子を渡す。未指定時はモデル引数を付けない。prompt 本文中のモデル名は起動設定にしない。同じ block の `auto_approve: true|false` は明示 boolean で、presence flag にはしない。未指定時は `auto_approve` 引数を渡さず Facade 既定（true）を使う。

## Cursor CLI

- **CLI バージョン:** `2026.11.22-e9042b2`
- **取得コマンド:** `cursor-agent models`
- **件数:** 284

`cursor-agent` の stream-json `init` イベントが返す `model` フィールドは UI 表示名である。例: 識別子 `composer-2.5-fast` に対し init では `Composer 2.5 Fast` が返る。

| CLI 識別子 (`model:` / `--model`) | UI 表示名 |
| --- | --- |
| `auto` | Auto (default) |
| `gpt-5.3-codex-low` | Codex 5.3 Low |
| `gpt-5.3-codex-low-fast` | Codex 5.3 Low Fast |
| `gpt-5.3-codex` | Codex 5.3 |
| `gpt-5.3-codex-fast` | Codex 5.3 Fast |
| `gpt-5.3-codex-high` | Codex 5.3 High |
| `gpt-5.3-codex-high-fast` | Codex 5.3 High Fast |
| `gpt-5.3-codex-xhigh` | Codex 5.3 Extra High |
| `gpt-5.3-codex-xhigh-fast` | Codex 5.3 Extra High Fast |
| `gpt-5.2` | GPT-5.2 |
| `composer-2.5` | Composer 2.5 |
| `claude-opus-5-thinking-high` | Claude Opus 5 1M Thinking |
| `claude-opus-5-thinking-high-fast` | Claude Opus 5 1M Thinking Fast |
| `gpt-5.6-sol-high` | GPT-5.6 Sol 1M High |
| `gpt-5.6-sol-high-fast` | GPT-5.6 Sol 1M High Fast |
| `gpt-5.6-sol-xhigh` | GPT-5.6 Sol 1M Extra High |
| `gpt-5.6-sol-xhigh-fast` | GPT-5.6 Sol 1M Extra High Fast |
| `claude-fable-5-thinking-high` | Claude Fable 5 1M Thinking (NO ZDR) |
| `claude-fable-5-thinking-xhigh` | Claude Fable 5 1M Extra High Thinking (NO ZDR) |
| `cursor-grok-4.5-high` | Grok 4.5 |
| `cursor-grok-4.5-high-fast` | Grok 4.5 Fast |
| `claude-sonnet-5-thinking-high` | Claude Sonnet 5 1M Thinking |
| `claude-sonnet-5-thinking-xhigh` | Claude Sonnet 5 1M Extra High Thinking |
| `gpt-5.6-luna-high` | GPT-5.6 Luna 1M High |
| `grok-4.7-low` | Grok 4.7  Low |
| `grok-4.7-low-fast` | Grok 4.7  Low Fast |
| `grok-4.7-medium` | Grok 4.7  Medium |
| `grok-4.7-medium-fast` | Grok 4.7  Medium Fast |
| `grok-4.7-high` | Grok 4.7  High |
| `grok-4.7-high-fast` | Grok 4.7  High Fast |
| `grok-4.7-xhigh` | Grok 4.7  Extra High |
| `grok-4.7-xhigh-fast` | Grok 4.7  Extra High Fast |
| `cursor-grok-4.6-low` | Grok 4.6 Low |
| `cursor-grok-4.6-low-fast` | Grok 4.6 Low Fast |
| `cursor-grok-4.6-medium` | Grok 4.6 Medium |
| `cursor-grok-4.6-medium-fast` | Grok 4.6 Medium Fast |
| `cursor-grok-4.6-high` | Grok 4.6 |
| `cursor-grok-4.6-high-fast` | Grok 4.6 Fast |
| `cursor-grok-4.6-xhigh` | Grok 4.6 Extra High |
| `cursor-grok-4.6-xhigh-fast` | Grok 4.6 Extra High Fast |
| `composer-2.5-fast` | Composer 2.5 Fast |
| `claude-opus-5-5-low` | Claude Opus 5.5 1M Low |
| `claude-opus-5-5-low-fast` | Claude Opus 5.5 1M Low Fast |
| `claude-opus-5-5-medium` | Claude Opus 5.5 1M |
| `claude-opus-5-5-medium-fast` | Claude Opus 5.5 1M Fast |
| `claude-opus-5-5-high` | Claude Opus 5.5 1M High |
| `claude-opus-5-5-high-fast` | Claude Opus 5.5 1M High Fast |
| `claude-opus-5-5-xhigh` | Claude Opus 5.5 1M Extra High |
| `claude-opus-5-5-xhigh-fast` | Claude Opus 5.5 1M Extra High Fast |
| `claude-opus-5-5-max` | Claude Opus 5.5 1M Max |
| `claude-opus-5-5-max-fast` | Claude Opus 5.5 1M Max Fast |
| `claude-opus-5-low` | Claude Opus 5 1M Low |
| `claude-opus-5-low-fast` | Claude Opus 5 1M Low Fast |
| `claude-opus-5-medium` | Claude Opus 5 1M Medium |
| `claude-opus-5-medium-fast` | Claude Opus 5 1M Medium Fast |
| `claude-opus-5-high` | Claude Opus 5 1M |
| `claude-opus-5-high-fast` | Claude Opus 5 1M Fast |
| `claude-opus-5-thinking-low` | Claude Opus 5 1M Low Thinking |
| `claude-opus-5-thinking-low-fast` | Claude Opus 5 1M Low Thinking Fast |
| `claude-opus-5-thinking-medium` | Claude Opus 5 1M Medium Thinking |
| `claude-opus-5-thinking-medium-fast` | Claude Opus 5 1M Medium Thinking Fast |
| `claude-opus-5-thinking-xhigh` | Claude Opus 5 1M Extra High Thinking |
| `claude-opus-5-thinking-xhigh-fast` | Claude Opus 5 1M Extra High Thinking Fast |
| `claude-opus-5-thinking-max` | Claude Opus 5 1M Max Thinking |
| `claude-opus-5-thinking-max-fast` | Claude Opus 5 1M Max Thinking Fast |
| `claude-opus-4-8-low` | Claude Opus 4.8 1M Low |
| `claude-opus-4-8-low-fast` | Claude Opus 4.8 1M Low Fast |
| `claude-opus-4-8-medium` | Claude Opus 4.8 1M Medium |
| `claude-opus-4-8-medium-fast` | Claude Opus 4.8 1M Medium Fast |
| `claude-opus-4-8-high` | Claude Opus 4.8 1M |
| `claude-opus-4-8-high-fast` | Claude Opus 4.8 1M Fast |
| `claude-opus-4-8-xhigh` | Claude Opus 4.8 1M Extra High |
| `claude-opus-4-8-xhigh-fast` | Claude Opus 4.8 1M Extra High Fast |
| `claude-opus-4-8-max` | Claude Opus 4.8 1M Max |
| `claude-opus-4-8-max-fast` | Claude Opus 4.8 1M Max Fast |
| `claude-opus-4-8-thinking-low` | Claude Opus 4.8 1M Low Thinking |
| `claude-opus-4-8-thinking-low-fast` | Claude Opus 4.8 1M Low Thinking Fast |
| `claude-opus-4-8-thinking-medium` | Claude Opus 4.8 1M Medium Thinking |
| `claude-opus-4-8-thinking-medium-fast` | Claude Opus 4.8 1M Medium Thinking Fast |
| `claude-opus-4-8-thinking-high` | Claude Opus 4.8 1M Thinking |
| `claude-opus-4-8-thinking-high-fast` | Claude Opus 4.8 1M Thinking Fast |
| `claude-opus-4-8-thinking-xhigh` | Claude Opus 4.8 1M Extra High Thinking |
| `claude-opus-4-8-thinking-xhigh-fast` | Claude Opus 4.8 1M Extra High Thinking Fast |
| `claude-opus-4-8-thinking-max` | Claude Opus 4.8 1M Max Thinking |
| `claude-opus-4-8-thinking-max-fast` | Claude Opus 4.8 1M Max Thinking Fast |
| `gemini-4-neon-low` | Gemini 4 Neon  Low |
| `gemini-4-neon-medium` | Gemini 4 Neon  Medium |
| `gemini-4-neon-high` | Gemini 4 Neon  High |
| `gemini-4-neon-xhigh` | Gemini 4 Neon  Extra High |
| `gemini-4-carbon-low` | Gemini 4 Carbon  Low |
| `gemini-4-carbon-medium` | Gemini 4 Carbon  Medium |
| `gemini-4-carbon-high` | Gemini 4 Carbon  High |
| `gemini-4-hydrogen-low` | Gemini 4 Hydrogen  Low |
| `gemini-4-hydrogen-medium` | Gemini 4 Hydrogen  Medium |
| `gemini-4-hydrogen-high` | Gemini 4 Hydrogen  High |
| `gemini-4-hydrogen-xhigh` | Gemini 4 Hydrogen  Extra High |
| `gemini-4-argon-low` | Gemini 4 Argon  Low |
| `gemini-4-argon-medium` | Gemini 4 Argon  Medium |
| `gemini-4-argon-high` | Gemini 4 Argon  High |
| `muse-wonder-1.0-minimal` | Muse Wonder  Minimal |
| `muse-wonder-1.0-low` | Muse Wonder  Low |
| `muse-wonder-1.0-medium` | Muse Wonder  Medium |
| `muse-wonder-1.0-high` | Muse Wonder  High |
| `muse-wonder-1.0-xhigh` | Muse Wonder  Extra High |
| `muse-wonder-1.0-max` | Muse Wonder  Max |
| `gpt-5.6-sol-none` | GPT-5.6 Sol 1M None |
| `gpt-5.6-sol-none-fast` | GPT-5.6 Sol 1M None Fast |
| `gpt-5.6-sol-low` | GPT-5.6 Sol 1M Low |
| `gpt-5.6-sol-low-fast` | GPT-5.6 Sol 1M Low Fast |
| `gpt-5.6-sol-medium` | GPT-5.6 Sol 1M |
| `gpt-5.6-sol-medium-fast` | GPT-5.6 Sol 1M Fast |
| `gpt-5.6-sol-max` | GPT-5.6 Sol 1M Max |
| `gpt-5.6-sol-max-fast` | GPT-5.6 Sol 1M Max Fast |
| `gpt-5.5-none` | GPT-5.5 1M None |
| `gpt-5.5-none-fast` | GPT-5.5 None Fast |
| `gpt-5.5-low` | GPT-5.5 1M Low |
| `gpt-5.5-low-fast` | GPT-5.5 Low Fast |
| `gpt-5.5-medium` | GPT-5.5 1M |
| `gpt-5.5-medium-fast` | GPT-5.5 Fast |
| `gpt-5.5-high` | GPT-5.5 1M High |
| `gpt-5.5-high-fast` | GPT-5.5 High Fast |
| `gpt-5.5-extra-high` | GPT-5.5 1M Extra High |
| `gpt-5.5-extra-high-fast` | GPT-5.5 Extra High Fast |
| `claude-fable-5-2-low` | Claude Fable 5.2 1M Low (NO ZDR) |
| `claude-fable-5-2-low-fast` | Claude Fable 5.2 1M Low Fast (NO ZDR) |
| `claude-fable-5-2-medium` | Claude Fable 5.2 1M Medium (NO ZDR) |
| `claude-fable-5-2-medium-fast` | Claude Fable 5.2 1M Medium Fast (NO ZDR) |
| `claude-fable-5-2-high` | Claude Fable 5.2 1M (NO ZDR) |
| `claude-fable-5-2-high-fast` | Claude Fable 5.2 1M Fast (NO ZDR) |
| `claude-fable-5-2-xhigh` | Claude Fable 5.2 1M Extra High (NO ZDR) |
| `claude-fable-5-2-xhigh-fast` | Claude Fable 5.2 1M Extra High Fast (NO ZDR) |
| `claude-fable-5-2-max` | Claude Fable 5.2 1M Max (NO ZDR) |
| `claude-fable-5-2-max-fast` | Claude Fable 5.2 1M Max Fast (NO ZDR) |
| `claude-fable-5-1-low` | Claude Fable 5.1 1M Low (NO ZDR) |
| `claude-fable-5-1-medium` | Claude Fable 5.1 1M Medium (NO ZDR) |
| `claude-fable-5-1-high` | Claude Fable 5.1 1M (NO ZDR) |
| `claude-fable-5-1-xhigh` | Claude Fable 5.1 1M Extra High (NO ZDR) |
| `claude-fable-5-1-max` | Claude Fable 5.1 1M Max (NO ZDR) |
| `claude-fable-5-1-thinking-low` | Claude Fable 5.1 1M Low Thinking (NO ZDR) |
| `claude-fable-5-1-thinking-medium` | Claude Fable 5.1 1M Medium Thinking (NO ZDR) |
| `claude-fable-5-1-thinking-high` | Claude Fable 5.1 1M Thinking (NO ZDR) |
| `claude-fable-5-1-thinking-xhigh` | Claude Fable 5.1 1M Extra High Thinking (NO ZDR) |
| `claude-fable-5-1-thinking-max` | Claude Fable 5.1 1M Max Thinking (NO ZDR) |
| `claude-fable-5-low` | Claude Fable 5 1M Low (NO ZDR) |
| `claude-fable-5-medium` | Claude Fable 5 1M Medium (NO ZDR) |
| `claude-fable-5-high` | Claude Fable 5 1M (NO ZDR) |
| `claude-fable-5-xhigh` | Claude Fable 5 1M Extra High (NO ZDR) |
| `claude-fable-5-max` | Claude Fable 5 1M Max (NO ZDR) |
| `claude-fable-5-thinking-low` | Claude Fable 5 1M Low Thinking (NO ZDR) |
| `claude-fable-5-thinking-medium` | Claude Fable 5 1M Medium Thinking (NO ZDR) |
| `claude-fable-5-thinking-max` | Claude Fable 5 1M Max Thinking (NO ZDR) |
| `cursor-grok-4.5-low` | Grok 4.5 Low |
| `cursor-grok-4.5-low-fast` | Grok 4.5 Low Fast |
| `cursor-grok-4.5-medium` | Grok 4.5 Medium |
| `cursor-grok-4.5-medium-fast` | Grok 4.5 Medium Fast |
| `gemini-3.8-flash-low` | Gemini 3.8 Flash Low |
| `gemini-3.8-flash-medium` | Gemini 3.8 Flash Medium |
| `gemini-3.8-flash-high` | Gemini 3.8 Flash High |
| `muse-spark-1.3-minimal` | Muse Spark 1.3 1M Minimal |
| `muse-spark-1.3-low` | Muse Spark 1.3 1M Low |
| `muse-spark-1.3-medium` | Muse Spark 1.3 1M Medium |
| `muse-spark-1.3-high` | Muse Spark 1.3 1M |
| `muse-spark-1.3-xhigh` | Muse Spark 1.3 1M Extra High |
| `muse-spark-1.3-max` | Muse Spark 1.3 1M Max |
| `gpt-5.6-terra-none` | GPT-5.6 Terra 1M None |
| `gpt-5.6-terra-none-fast` | GPT-5.6 Terra 1M None Fast |
| `gpt-5.6-terra-low` | GPT-5.6 Terra 1M Low |
| `gpt-5.6-terra-low-fast` | GPT-5.6 Terra 1M Low Fast |
| `gpt-5.6-terra-medium` | GPT-5.6 Terra 1M |
| `gpt-5.6-terra-medium-fast` | GPT-5.6 Terra 1M Fast |
| `gpt-5.6-terra-high` | GPT-5.6 Terra 1M High |
| `gpt-5.6-terra-high-fast` | GPT-5.6 Terra 1M High Fast |
| `gpt-5.6-terra-xhigh` | GPT-5.6 Terra 1M Extra High |
| `gpt-5.6-terra-xhigh-fast` | GPT-5.6 Terra 1M Extra High Fast |
| `gpt-5.6-terra-max` | GPT-5.6 Terra 1M Max |
| `gpt-5.6-terra-max-fast` | GPT-5.6 Terra 1M Max Fast |
| `claude-sonnet-5-5-low` | Claude Sonnet 5.5  Low |
| `claude-sonnet-5-5-medium` | Claude Sonnet 5.5  Medium |
| `claude-sonnet-5-5-high` | Claude Sonnet 5.5  High |
| `claude-sonnet-5-5-xhigh` | Claude Sonnet 5.5  Extra High |
| `claude-sonnet-5-5-max` | Claude Sonnet 5.5  Max |
| `claude-sonnet-5-low` | Claude Sonnet 5 1M Low |
| `claude-sonnet-5-medium` | Claude Sonnet 5 1M Medium |
| `claude-sonnet-5-high` | Claude Sonnet 5 1M |
| `claude-sonnet-5-xhigh` | Claude Sonnet 5 1M Extra High |
| `claude-sonnet-5-max` | Claude Sonnet 5 1M Max |
| `claude-sonnet-5-thinking-low` | Claude Sonnet 5 1M Low Thinking |
| `claude-sonnet-5-thinking-medium` | Claude Sonnet 5 1M Medium Thinking |
| `claude-sonnet-5-thinking-max` | Claude Sonnet 5 1M Max Thinking |
| `claude-4.6-sonnet-medium` | Claude Sonnet 4.6 1M |
| `claude-4.6-sonnet-medium-thinking` | Claude Sonnet 4.6 1M Thinking |
| `claude-opus-4-7-low` | Claude Opus 4.7 1M Low |
| `claude-opus-4-7-low-fast` | Claude Opus 4.7 1M Low Fast |
| `claude-opus-4-7-medium` | Claude Opus 4.7 1M Medium |
| `claude-opus-4-7-medium-fast` | Claude Opus 4.7 1M Medium Fast |
| `claude-opus-4-7-high` | Claude Opus 4.7 1M High |
| `claude-opus-4-7-high-fast` | Claude Opus 4.7 1M High Fast |
| `claude-opus-4-7-xhigh` | Claude Opus 4.7 1M |
| `claude-opus-4-7-xhigh-fast` | Claude Opus 4.7 1M Fast |
| `claude-opus-4-7-max` | Claude Opus 4.7 1M Max |
| `claude-opus-4-7-max-fast` | Claude Opus 4.7 1M Max Fast |
| `claude-opus-4-7-thinking-low` | Claude Opus 4.7 1M Low Thinking |
| `claude-opus-4-7-thinking-low-fast` | Claude Opus 4.7 1M Low Thinking Fast |
| `claude-opus-4-7-thinking-medium` | Claude Opus 4.7 1M Medium Thinking |
| `claude-opus-4-7-thinking-medium-fast` | Claude Opus 4.7 1M Medium Thinking Fast |
| `claude-opus-4-7-thinking-high` | Claude Opus 4.7 1M High Thinking |
| `claude-opus-4-7-thinking-high-fast` | Claude Opus 4.7 1M High Thinking Fast |
| `claude-opus-4-7-thinking-xhigh` | Claude Opus 4.7 1M Thinking |
| `claude-opus-4-7-thinking-xhigh-fast` | Claude Opus 4.7 1M Thinking Fast |
| `claude-opus-4-7-thinking-max` | Claude Opus 4.7 1M Max Thinking |
| `claude-opus-4-7-thinking-max-fast` | Claude Opus 4.7 1M Max Thinking Fast |
| `gpt-5.4-low` | GPT-5.4 1M Low |
| `gpt-5.4-medium` | GPT-5.4 1M |
| `gpt-5.4-medium-fast` | GPT-5.4 Fast |
| `gpt-5.4-high` | GPT-5.4 1M High |
| `gpt-5.4-high-fast` | GPT-5.4 High Fast |
| `gpt-5.4-xhigh` | GPT-5.4 1M Extra High |
| `gpt-5.4-xhigh-fast` | GPT-5.4 Extra High Fast |
| `claude-4.6-opus-high` | Claude Opus 4.6 1M |
| `claude-4.6-opus-max` | Claude Opus 4.6 1M Max |
| `claude-4.6-opus-high-thinking` | Claude Opus 4.6 1M Thinking |
| `claude-4.6-opus-max-thinking` | Claude Opus 4.6 1M Max Thinking |
| `claude-4.5-opus-high` | Claude Opus 4.5 |
| `claude-4.5-opus-high-thinking` | Claude Opus 4.5 Thinking |
| `gpt-5.2-low` | GPT-5.2 Low |
| `gpt-5.2-low-fast` | GPT-5.2 Low Fast |
| `gpt-5.2-fast` | GPT-5.2 Fast |
| `gpt-5.2-high` | GPT-5.2 High |
| `gpt-5.2-high-fast` | GPT-5.2 High Fast |
| `gpt-5.2-xhigh` | GPT-5.2 Extra High |
| `gpt-5.2-xhigh-fast` | GPT-5.2 Extra High Fast |
| `gpt-5.6-luna-none` | GPT-5.6 Luna 1M None |
| `gpt-5.6-luna-none-fast` | GPT-5.6 Luna 1M None Fast |
| `gpt-5.6-luna-low` | GPT-5.6 Luna 1M Low |
| `gpt-5.6-luna-low-fast` | GPT-5.6 Luna 1M Low Fast |
| `gpt-5.6-luna-medium` | GPT-5.6 Luna 1M |
| `gpt-5.6-luna-medium-fast` | GPT-5.6 Luna 1M Fast |
| `gpt-5.6-luna-high-fast` | GPT-5.6 Luna 1M High Fast |
| `gpt-5.6-luna-xhigh` | GPT-5.6 Luna 1M Extra High |
| `gpt-5.6-luna-xhigh-fast` | GPT-5.6 Luna 1M Extra High Fast |
| `gpt-5.6-luna-max` | GPT-5.6 Luna 1M Max |
| `gpt-5.6-luna-max-fast` | GPT-5.6 Luna 1M Max Fast |
| `gemini-3.1-pro` | Gemini 3.1 Pro |
| `gpt-5.4-mini-none` | GPT-5.4 Mini None |
| `gpt-5.4-mini-low` | GPT-5.4 Mini Low |
| `gpt-5.4-mini-medium` | GPT-5.4 Mini |
| `gpt-5.4-mini-high` | GPT-5.4 Mini High |
| `gpt-5.4-mini-xhigh` | GPT-5.4 Mini Extra High |
| `gpt-5.4-nano-none` | GPT-5.4 Nano None |
| `gpt-5.4-nano-low` | GPT-5.4 Nano Low |
| `gpt-5.4-nano-medium` | GPT-5.4 Nano |
| `gpt-5.4-nano-high` | GPT-5.4 Nano High |
| `gpt-5.4-nano-xhigh` | GPT-5.4 Nano Extra High |
| `claude-haiku-5-5-low` | Claude Haiku 5.5  Low No Thinking |
| `claude-haiku-5-5-medium` | Claude Haiku 5.5  Medium No Thinking |
| `claude-haiku-5-5-high` | Claude Haiku 5.5  High No Thinking |
| `claude-haiku-5-5-xhigh` | Claude Haiku 5.5  Extra High No Thinking |
| `claude-haiku-5-5-max` | Claude Haiku 5.5  Max No Thinking |
| `claude-haiku-5-5-thinking-low` | Claude Haiku 5.5  Low |
| `claude-haiku-5-5-thinking-medium` | Claude Haiku 5.5  Medium |
| `claude-haiku-5-5-thinking-high` | Claude Haiku 5.5  High |
| `claude-haiku-5-5-thinking-xhigh` | Claude Haiku 5.5  Extra High |
| `claude-haiku-5-5-thinking-max` | Claude Haiku 5.5  Max |
| `claude-4.5-sonnet` | Claude Sonnet 4.5 |
| `claude-4.5-sonnet-thinking` | Claude Sonnet 4.5 Thinking |
| `gpt-5.1-low` | GPT-5.1 Low |
| `gpt-5.1` | GPT-5.1 |
| `gpt-5.1-high` | GPT-5.1 High |
| `gemini-3-flash` | Gemini 3 Flash |
| `gemini-3.5-flash` | Gemini 3.5 Flash |
| `claude-4-sonnet` | Claude Sonnet 4 |
| `claude-4-sonnet-thinking` | Claude Sonnet 4 Thinking |
| `gpt-5-mini` | GPT-5 Mini |
| `kimi-k3-low` | Kimi K3 Low |
| `kimi-k3-high` | Kimi K3 High |
| `kimi-k3-max` | Kimi K3 |
| `kimi-k2.7-code` | Kimi K2.7 Code |
| `glm-5.2-high` | GLM 5.2 |
| `glm-5.2-max` | GLM 5.2 Max |
| `deepseek-v41-flash-1017-none` | DeepSeek V4.1 Flash 1017 None |
| `deepseek-v41-flash-1017-low` | DeepSeek V4.1 Flash 1017 Low |
| `deepseek-v41-flash-1017-medium` | DeepSeek V4.1 Flash 1017 Medium |
| `deepseek-v41-flash-1017-high` | DeepSeek V4.1 Flash 1017 High |
| `deepseek-v41-flash-1017-max` | DeepSeek V4.1 Flash 1017 Max |

- [Cursor CLI パラメーター](https://cursor.com/docs/cli/reference/parameters)

## GitHub Copilot CLI

- **CLI バージョン:** `1.0.114`
- **調査日:** 2026-11-23
- **取得方法:** ログイン済み CLI で `copilot --no-auto-update --model` を引数不足として実行し、CLI が出した `[possible values: ...]` を取得した
- **件数:** 32
- **注意:** 利用可能モデルは Copilot プラン、組織ポリシー、ロールアウト状態、認証アカウントにより変わる。下表は **この CLI とアカウント環境で `--model` の候補として出た一覧** である。
- **Fusion:** `fusion` は単一の AI model 名ではない。GitHub Copilot Fusion は prompt ごとに model と実行パターン（Single / Cascade / Critique）を選ぶ runtime router / orchestrator である。effort は Facade の `reasoning_effort` に `standard` / `high` / `max` をそのまま書かず、CLI が受理する値と対象 model の組み合わせを確認する。公式情報: [About GitHub Copilot Fusion](https://docs.github.com/en/copilot/concepts/models/copilot-fusion)
- **公式カタログとの差:** Gemini 4 Hydrogen、Gemini 4 Neon、MAI Code 2、MAI Code 2 Flash は公式に Copilot CLI 対応と案内されており、この環境の `--model` 候補には表示されなかった。[GitHub Changelog](https://github.blog/changelog/month/11-2026/)

| CLI 識別子 (`model:` / `--model`) | UI 表示名 |
| --- | --- |
| `auto` | Auto |
| `fusion` | Copilot Fusion |
| `claude-sonnet-5.5` | Claude Sonnet 5.5 |
| `claude-sonnet-5` | Claude Sonnet 5 |
| `claude-fable-5.2` | Claude Fable 5.2 |
| `claude-fable-5.1` | Claude Fable 5.1 |
| `claude-fable-5` | Claude Fable 5 |
| `claude-opus-5.5` | Claude Opus 5.5 |
| `claude-opus-5` | Claude Opus 5 |
| `claude-opus-4.8` | Claude Opus 4.8 |
| `claude-opus-4.8-fast` | Claude Opus 4.8 (fast mode) |
| `claude-sonnet-4.6` | Claude Sonnet 4.6 |
| `claude-haiku-5.5` | Claude Haiku 5.5 |
| `claude-haiku-4.5` | Claude Haiku 4.5 |
| `gpt-6.2-sol` | GPT-6.2 Sol |
| `gpt-6.2-astra` | GPT-6.2 Astra |
| `gpt-6.1-sol` | GPT-6.1 Sol |
| `gpt-6.1-terra` | GPT-6.1 Terra |
| `gpt-6-sol` | GPT-6 Sol |
| `gpt-6-luna` | GPT-6 Luna |
| `gpt-6-astra` | GPT-6 Astra |
| `gpt-5.6-sol` | GPT-5.6 Sol |
| `gpt-5.6-terra` | GPT-5.6 Terra |
| `gpt-5.6-luna` | GPT-5.6 Luna |
| `gpt-5.3-codex` | GPT-5.3-Codex |
| `mai-code-1.1-flash` | MAI-Code-1.1-Flash |
| `gemini-4-carbon` | Gemini 4 Carbon |
| `gemini-4-argon` | Gemini 4 Argon |
| `gemini-3.8-flash` | Gemini 3.8 Flash |
| `grok-4.7` | Grok 4.7 |
| `grok-4.6` | Grok 4.6 |
| `kimi-k3` | Kimi K3 |

**新規・更新情報:**

- `gpt-6.2-sol` と `gpt-6.2-astra` が候補に加わった。[GitHub Changelog](https://github.blog/changelog/2026-11-17-openais-gpt-6-2-astra-and-gpt-6-2-sol-now-available/)
- Claude Fable 5 / 5.1 / 5.2 は既定で Anthropic による安全性 classifier 用の data retention が必要で、Enterprise の条件と model policy 設定に依存する。
- 2026-12-14 に `claude-fable-5`、`claude-haiku-4.5`、`gemini-3.8-flash`、`gemini-4-argon`、`grok-4.6`、`kimi-k3` が廃止予定である（Haiku 4.5 は annual plan の個人契約者を除く）。代替と最新状況は [GitHub Changelog](https://github.blog/changelog/2026-11-13-upcoming-deprecation-of-selected-github-copilot-models-in-december/) と [Supported AI models in GitHub Copilot](https://docs.github.com/en/copilot/reference/ai-models/supported-models) を確認する。

## Grok Build CLI

- **調査日:** 2026-11-23
- **CLI バージョン:** `grok 1.0.92 (b06756fd6256) [stable]`
- **更新確認:** `grok update --check --json` で stable の最新版 1.0.92 と一致
- **取得コマンド:** `grok models`（grok.com にログイン済み）
- **件数:** 4

| CLI 識別子 (`model:` / `--model`) | 備考 |
| --- | --- |
| `grok-4.7` | CLI カタログの既定値 |
| `grok-4.7-build-fast` | Grok 4.7 Fast。利用可否はプランに依存する。[Grok 4.7 Developer Guide](https://docs.x.ai/developers/grok-4-7) |
| `grok-4.6` | ログイン済みカタログの候補 |
| `grok-4.5` | ログイン済みカタログの候補 |

`grok-4.7-build-fast` は `model` に指定する CLI 識別子である。reasoning effort は `--reasoning-effort`（`--effort`）で渡す。

### reasoning effort

以下の実推論結果は 2026-10-14 から 10-15 の Grok Build 1.0.52 での観測。2026-11-23 に 1.0.92 で `invalid` を渡した場合も、CLI は `xhigh`、`high`、`medium`、`low` を候補として表示して終了した。

Facade は `reasoning_effort` を `--reasoning-effort` へそのまま渡す。Cursor のようなカタログ合成はしない。

このログイン状態の `grok-4.7` で、session `summary.json` の `current_model_id` と `reasoning_effort` を確認した。

| 指定 | 結果 |
| --- | --- |
| model 未指定 | `current_model_id=grok-4.7`、`reasoning_effort=high`。この環境の `~/.grok/config.toml` は `default_reasoning_effort = "high"` |
| `--model grok-4.7` のみ | `grok-4.7` / `high` |
| `--reasoning-effort low` / `medium` / `high` / `xhigh` | それぞれ session の `reasoning_effort` がその値になった。stderr の無視警告は無し |
| `none` / `minimal` / `max` / `not-a-level` | exit 1。stderr は `unknown effort level '<値>'; use one of: xhigh, high, medium, low` |
| `--model grok-4.6 --reasoning-effort low` | `grok-4.6` はカタログに無い。stderr に `warning: grok-4.6 isn't in the model catalog; ignoring --effort/--reasoning-effort low` が出て、続けて model 設定失敗で exit 1 |

`model does not support reasoning effort; ignoring` は 1.0.52 のバイナリにも、上記の実機 stderr にも無かった。カタログ外モデルの無視警告は exit 1 なので、Facade は従来どおり `non_zero_exit` で失敗する。exit 0 で新しい無視警告だけが出た場合は `option_not_applied` にする。`--effort/--reasoning-effort ignored` は同じバイナリの文字列としてあるが、当時のカタログでは exit 0 の再現は取れていない。

## Antigravity CLI

- **CLI バージョン:** `agy` 1.5.14
- **取得コマンド:** `agy models`
- **件数:** 23
- **確認日:** 2026-11-23

`model:` には、このコマンドが出した slug をそのまま書く。Facade は空白以外を `--model` へ変換せず渡す。省略時は引数を付けない。未知 slug を別モデルへ替えない。slug の末尾にある level 名は model 識別子の一部であり、`reasoning_effort` ではない。

| CLI 識別子 (`model:` / `--model`) | 表示名 |
| --- | --- |
| `gemini-4-neon-high` | Gemini 4 Neon (High) |
| `gemini-4-neon-xhigh` | Gemini 4 Neon (XHigh) |
| `gemini-4-neon-medium` | Gemini 4 Neon (Medium) |
| `gemini-4-neon-low` | Gemini 4 Neon (Low) |
| `gemini-4-argon-medium` | Gemini 4 Argon (Medium) |
| `gemini-4-argon-high` | Gemini 4 Argon (High) |
| `gemini-4-argon-low` | Gemini 4 Argon (Low) |
| `gemini-4-hydrogen-high` | Gemini 4 Hydrogen (High) |
| `gemini-4-hydrogen-xhigh` | Gemini 4 Hydrogen (XHigh) |
| `gemini-4-hydrogen-medium` | Gemini 4 Hydrogen (Medium) |
| `gemini-4-hydrogen-low` | Gemini 4 Hydrogen (Low) |
| `gemini-pro-orchestrator-neon` | Orchestrator (Neon) |
| `gemini-pro-orchestrator-g1-pro` | Orchestrator (Argon) |
| `gemini-3.8-flash-high` | Gemini 3.8 Flash (High) |
| `gemini-3.8-flash-medium` | Gemini 3.8 Flash (Medium) |
| `gemini-3.8-flash-low` | Gemini 3.8 Flash (Low) |
| `claude-opus-5-5-low` | Claude Opus 5.5 (Low) |
| `claude-opus-5-5-medium` | Claude Opus 5.5 (Medium) |
| `claude-opus-5-5-high` | Claude Opus 5.5 (High) |
| `claude-sonnet-5-5-low` | Claude Sonnet 5.5 (Low) |
| `claude-sonnet-5-5-medium` | Claude Sonnet 5.5 (Medium) |
| `claude-sonnet-5-5-high` | Claude Sonnet 5.5 (High) |
| `gpt-oss-120b-medium` | GPT-OSS 120B (Medium) |

### 追加モデルと認証方式によるモデル差分

この環境では Gemini 4 Neon の 4 段階と Orchestrator (Neon) が加わった。Google AI Ultra、Google AI Pro、Gemini Enterprise で Gemini 4 Neon が提供され、モデル未選択時の既定値となる。認証方式による追加は `agy changelog` の 1.5.5、1.5.8、1.5.12、1.5.13 を参照。[Google Antigravity Changelog](https://antigravity.google/docs/changelog)

- Gemini 4 Neon の Low、Medium、High、XHigh は、`GEMINI_API_KEY` 接続、`AGY_ADC_AUTH` 接続、Enterprise model gateway でも提供される。
- `GEMINI_API_KEY` 接続時: Gemini 4 Hydrogen (Low, Medium, High, XHigh) および Gemini 4 Carbon (Low, Medium, High) が `/model` と `--model` に追加される。
- `AGY_ADC_AUTH`（Google Cloud Application Default Credentials）接続時: Gemini 4 Hydrogen (Low, Medium, High, XHigh) および Gemini 4 Carbon (Low, Medium, High) が追加される。
- Enterprise model gateway 経由: Gemini 4 Hydrogen がサポートされる。

### Reasoning Effort と無効値の検証

`reasoning_effort` は `--effort` へそのまま渡す。`agy --help` および実機でのエラー出力により、受け付ける effort トークンは `low`, `medium`, `high`, `xhigh`, `max` であることが確認されている。

| 指定 | 結果 |
| --- | --- |
| `--effort invalid` | exit 1。stderr: `error: invalid model selection (--model "" --effort "invalid"): invalid --effort "invalid" (valid: low, medium, high, xhigh, max)` |
| `--model invalid-model` | exit 1。stderr: `error: invalid model selection (--model "invalid-model" --effort ""): model invalid-model is not recognized as a known model or custom model in settings`（続けて `Available models:` として有効な表示名一覧が出力される） |

Facade は token を絞らず、未指定なら `--effort` を付けない。

`fast` は非対応である。relay は送らない。指定されていれば起動前に拒否し、黙って省略しない。`run_mode=plan` は `--mode plan` になる。通常実行では `--mode` を付けない。成功時の `outputKind` は `plan` で、`planPath` は付けない。`plan` 以外の `run_mode` は起動前に拒否する。`auto_approve: true` は `--dangerously-skip-permissions`、`false` はその flag を付けない。`false` は headless の対話承認にはならず、tool が soft-deny され得る。未指定は引数を渡さず Facade 既定の true になる。`session_id` は Antigravity conversation id で、継続するときだけ `--conversation` に渡る。

- [Headless](https://antigravity.google/docs/cli/headless)

## Codex CLI

- **CLI バージョン:** `codex-cli 0.171.0`
- **確認日:** 2026-11-23
- **取得方法:** ChatGPT ログイン済みの `codex app-server --listen stdio://` を初期化し、`model/list` に `includeHidden: false` を渡す。表の識別子は応答の `data[].model`。
- **件数:** 11

選択候補は接続先やアカウント設定に依存する。表の Fast はカタログに `fast` が表示された項目を示す。利用権限と実行時の提供状況は接続先に依存する。[Codex app-server](https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server)

| CLI 識別子 (`model:` / `--model`) | 表示名 | 既定 effort | 対応 effort | Fast |
| --- | --- | --- | --- | --- |
| `gpt-6.2-sol` | GPT-6.2-Sol（カタログ既定） | `low` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-6.2-astra` | GPT-6.2-Astra | `low` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-6.1-terra` | GPT-6.1 Terra | `low` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-6.1-sol` | GPT-6.1-Sol | `low` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-6-astra` | GPT-6-Astra | `low` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-6-sol` | GPT-6-Sol | `medium` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-6-luna` | GPT-6-Luna | `medium` | `low`, `medium`, `high`, `xhigh`, `max` | あり |
| `gpt-5.6-sol` | GPT-5.6-Sol | `low` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-5.6-terra` | GPT-5.6-Terra | `medium` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | あり |
| `gpt-5.6-luna` | GPT-5.6-Luna | `medium` | `low`, `medium`, `high`, `xhigh`, `max` | あり |
| `gpt-daybreak-blue-latest` | Daybreak Blue | `low` | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` | — |

## Jules

Jules に model 識別子は無い。`model`、`reasoning_effort`、`fast`、`run_mode`、空でない `skills`、`auto_approve=false` は起動前に拒否する。relay はこれらを省略して送ったり、別の値へ替えたりしない。新規 session では `session_id` を渡さない。[Jules REST API](https://jules.google/docs/api/reference/)

## 更新方法

1. **Cursor:** `cursor-agent models` を実行し、`識別子 - 表示名` 形式の出力を反映する。CLI は一部行を特殊な区切り文字で連結するため、連結された行は区切ってから表へ変換する。識別子は CLI 出力から得た値だけを使い、推測で補わない。
2. **GitHub Copilot:** 対話 CLI で `/model` を実行し、script では `copilot --model` の引数不足エラーに出る `possible values` を `copilot --no-auto-update --model` から取得する。候補が実際に指定可能かは model policy と rollout に左右されるため、公式の [Supported AI models](https://docs.github.com/en/copilot/reference/ai-models/supported-models) と [GitHub Copilot CLI programmatic reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference) も確認する。
3. **Grok Build:** `grok models` を実行し、CLI が出した識別子だけを反映する。未ログイン時のカタログと、ログイン後に実際に選べる集合は分けて記録する。
4. **Antigravity:** `agy models` を実行し、CLI が出した slug だけを反映する。effort の受理範囲は公開資料と、その model が拒否した実エラーを分けて記録する。推測で token を足さない。
5. **Codex:** `codex app-server` の `model/list` で `includeHidden: false` を指定し、`nextCursor` があれば全ページを取得する。`data[].model` と effort・速度の情報を反映する。
6. このファイルのスナップショット日、CLI バージョン、表本体を更新する。

## 関連

- relay SKILL の `model:` 仕様: [cursor](../apm-packages/cursor/.apm/skills/cursor/SKILL.md), [github-copilot](../apm-packages/github-copilot/.apm/skills/github-copilot/SKILL.md), [grok-build](../apm-packages/grok-build/.apm/skills/grok-build/SKILL.md), [antigravity](../apm-packages/antigravity/.apm/skills/antigravity/SKILL.md), [jules](../apm-packages/jules/.apm/skills/jules/SKILL.md)
- Facade の `model` 引数: [README](README.md) の `start_agent` 節

## Reasoning Effort と fast（issue #24 第2段階）

`model`, `reasoning_effort`, `fast` は独立した実行設定である。relay Skillでは委譲ヘッダの後、作業本文の前に `facade-options` blockを置く。ブロック自体は worker prompt に含めず、`$skill` の呼び出しも prompt と `skills` に入れない。MCPでは `start_agent` の同名引数で指定する。設定はprompt本文から推測せず、session継続とexact retryでも毎回必要な値を渡す。設定なしは既存CLI既定を保ち、`fast: false` は明示的な非-fast/default指定である。Copilot と Grok Build は独立した `fast` を受け付けない。`run_mode=plan` はモデル識別子ではない。Grok Build は通常実行の `--reasoning-effort` とは別に `session/set_config_option` の文字列値で model と reasoning_effort を適用する。Antigravity は `model` と `reasoning_effort` を `--model` と `--effort` へそのまま渡し、`run_mode=plan` のときだけ `--mode plan` を付ける。`fast` は拒否する。Jules は `model`、`reasoning_effort`、`fast`、`run_mode`、`auto_approve=false` を拒否する。

| Agent | Reasoning Effort | Independent fast | CLI変換 |
| --- | --- | --- | --- |
| Cursor | 実行直前の `cursor-agent models` にあるtoken | `true` / `false` | 同じ基底のカタログ識別子を1つ選んで `--model` に渡す。一致が無ければ起動しない |
| GitHub Copilot | `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max` | 非対応。両booleanを拒否 | `--reasoning-effort <token>` |
| Grok Build | CLI canonicalまたはmodel固有token | 非対応。両booleanを拒否 | `--reasoning-effort <token>`。CLIがeffortを無視したwarningを出せばexit 0でも失敗 |
| Codex CLI | CLIが許すeffort token | `true` / `false` | `-c model_reasoning_effort="<token>"`; fastは `-c service_tier="priority"` / `"default"` |
| Antigravity | `low`, `medium`, `high`, `xhigh`, `max`（未指定時は付与しない） | 非対応。両booleanを拒否 | `--model` と `--effort`。`run_mode=plan` は `--mode plan`。未指定は付けない |
| Jules | 非対応 | 非対応。両booleanを拒否 | `model`、`reasoning_effort`、`fast` は起動前に拒否 |

Cursorは `reasoning_effort` と `fast` を両方省略したとき、渡された `model` をそのまま `--model` に渡す。どちらかを指定したときは、その実行の直前に `cursor-agent models` を読み、基底・effort・fastが一致する識別子だけを渡す。bracketは作らない。一覧の取得に失敗した場合や、一致する識別子が無い場合は起動しない。モデル文字列に既に effort や `-fast` があり、同じ項目を独立fieldでも指定した場合は重複として拒否する。

`auto_approve=false` のCodexは `sandbox_mode=read-only` と `approval_policy=never` を使用する。そのためapproval promptは出ない。Codexのmodel/effort/tierをCLI引数へ渡したこと、CLI初期化ログが `service_tier=default` を記録したことはbackendがtierを提供した証拠ではない。今回 `fast=true` のCodex推論は実施していない。

Fastの利用条件と費用はproviderとmodelに依存する。OpenAIの現行Codex資料では、対応modelのFast modeはsubscription included usage limitをStandardの2.5倍で消費し、購入creditsおよびEnterprise pay-as-you-goはStandardの2倍で請求される。API key利用はChatGPT credit multiplierの対象外。Cursorはmodelごとに価格が異なり、掲載例のCursor内Grok 4.7 Fastは通常tierのinput/output価格が各2倍、Cursor内Composer 2.5 Fastはinputが6倍・outputが6倍となっている。これはCursor内の価格例であり、Grok Build CLIの価格ではない。価格とplan eligibilityは変わりうるため実行前にproviderの現行ページを確認する。

- [Codex Fast mode](https://learn.chatgpt.com/docs/agent-configuration/speed)
- [Codex config reference](https://learn.chatgpt.com/docs/config-file/config-reference)
- [Cursor Models and Pricing](https://cursor.com/docs/models-and-pricing)
- [Cursor CLI parameters](https://cursor.com/docs/cli/reference/parameters)
- [Cursor SDK model parameters](https://cursor.com/docs/sdk/typescript)
- [GitHub Copilot CLI reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)
- [Grok Build CLI option handling](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/src/headless.rs)

コード上の引数生成、MCP schema、mock runnerによるnew/resume/result/failure検証と、実CLIの実推論・backend tier提供確認は別の証拠である。2026-11-23 の更新ではカタログと CLI の入力検証を確認した。Grok Build の旧版での実推論の結果は各表に記載した。
