# 通常アプリ向け CLI

通常アプリは `CodexAgentFacadeCli.exe` を1回起動し、標準入力のJSON requestに対するJSON resultを標準出力から受け取る。agent固有の起動引数への変換は既存Driverが行い、CLIはそれを再実装しない。

この入口はMCPホストの代替サーバではない。常駐の `CodexAgentFacade.exe` は従来どおりloopback MCPを提供する。CLIは呼ばれたプロセスの寿命で1件のagent実行を終える。

## 起動

開発時:

```powershell
dotnet run --file src/CodexAgentFacadeCli.cs
```

通常アプリが使う配置済み入口は、publishしたコンソールexeである。SDKのビルドメッセージを結果のパイプへ混ぜない。

```powershell
dotnet publish src/CodexAgentFacadeCli.cs -c Release -r win-x64 --self-contained false -o D:\Tools\Development\CodexAgentFacade
```

`tools/publish-facade.cs` は、既存の配置フォルダへ WinExe、`RunLogViewer.exe`、`CodexAgentFacadeCli.exe` を一緒に入れる。タスクスケジューラが起動するのは引数なしの `CodexAgentFacade.exe` のままである。CLIはスケジューラの新しいインスタンスにも、別タスクにもしない。run log viewer の有効無効は `%USERPROFILE%\.codex-agent-facade\config.json` のままで、publishは変更しない。

配置の差し替えは、そのフォルダを使っている他の作業checkoutを上書きしない。発行先は `tools/publish-facade.local.json` の `deploymentDirectory` だけである。

CLIに `CODEX_AGENT_FACADE_TOKEN` は不要である。tokenは常駐MCPホストのBearer認証にだけ使う。

引数は受け取らない。コマンドライン引数があると、agentは起動せず終了コード2のJSONを返す。

## 入出力

標準入力はJSON objectを1個だけ書く。UTF-8。呼び出し側は書き終えたらstdinを閉じる。フィールド名はMCP `start_agent` の引数名に合わせ、snake_caseだけを受け付ける。未知のフィールドは拒否する。`request_id` を渡しても冪等実行にはならず、JSON自体が不正になる。

| フィールド | 必須 | 内容 |
| --- | --- | --- |
| `agent` | はい | `github-copilot`、`grok-build`、`cursor`、`codex` |
| `prompt` | はい | worker prompt。本文からmodel、effort、fastは推測しない |
| `working_directory` | はい | agent processの作業場所。継続時も毎回指定する |
| `session_id` | いいえ | 完了結果の `sessionId` による同一外部sessionの継続。省略時は新規 |
| `skills` | いいえ | 既存Driverの変換に従う。CursorとCodexはpromptへ変換しない。nullや空白の要素は起動前に `invalid_request` |
| `auto_approve` | いいえ | 省略時は true |
| `model` | いいえ | 省略時はCLIへmodel引数を付けない |
| `reasoning_effort` | いいえ | 省略時はCLI既定。非対応値は起動前に失敗する |
| `fast` | いいえ | 省略、true、false。CopilotとGrok Buildは true/false とも起動前に失敗する |
| `timeout_seconds` | いいえ | 1〜86400。指定時は期限でagent process treeを止める。省略時はCLI側の時間制限は無い |

標準出力は完了時にJSON objectを1個だけ書く。run logの本文、agentのraw stream、診断ログは書かない。agent実行の逐次出力は、結果が指す run log に残る。CLIプロセスの例外トレースは `%USERPROFILE%\.codex-agent-facade\cli.{processId}.log` に残る。常駐MCPホストは `server.log` をプロセス終了まで開いたままにするため、CLIはそのファイルへ書かない。複数のCLIはそれぞれ自分のファイルへ書く。agent実行の失敗JSONの `error` は、その呼び出しの `cli.{processId}.log` のパスを示す。`server.log` とは書かない。MCP jobの失敗案内は従来どおり server log を指す。標準エラーは、結果JSONを書けなかった起動失敗の1行にだけ使い、同じCLI診断ログのパスを示す。診断ログのファイルを開けない場合は、標準エラーに `Exception.ToString()` と、ログを作れなかったことを示す1行を書き、終了コード1で終わる。終了済みプロセスの `cli.*.log` は、実行中プロセスのファイルを残したまま、合わせて最大4件まで保持する。`server.log` は削除しない。

成功例:

```json
{"status":"completed","result":{"agent":"github-copilot","sessionId":"sess-copilot","exitCode":0,"outputText":"pong","runId":"20261014T000000Z-01234567","eventsLogPath":"C:\\Users\\example\\.codex-agent-facade\\runs\\20261014T000000Z-01234567.events.jsonl","textLogPath":"C:\\Users\\example\\.codex-agent-facade\\runs\\20261014T000000Z-01234567.log","outputKind":"final_response"}}
```

`result` はMCPの公開resultと同じ項目である。`rawOutput` は含めない。`outputKind` は `final_response` または `assistant_transcript`。`completed` は外部CLIが終了したことだけを示し、生成物の受入判断は呼び出し側が行う。

失敗例:

```json
{"status":"failed","error":"agent run failed. See the CLI diagnostic log for details: C:\\Users\\example\\.codex-agent-facade\\cli.4242.log","failure":{"kind":"non_zero_exit","summary":"GitHub Copilot CLI exited with code 7. stderr: fake agent failed","exitCode":7,"runId":"...","eventsLogPath":"...","textLogPath":"..."}}
```

`failure.kind` はMCPと同じ `process_start_failed`、`non_zero_exit`、`output_parse_failed`、`option_not_applied`、`session_mismatch`、`facade_interrupted`、`internal_error` に加え、CLIでは `invalid_request`、`timeout`、`cancelled` を使う。`summary` は改行を除いた最大512文字で、secret redactionを通す。失敗時の `summary` には、MCPと同様に短いstderr抜粋が含まれることがある。stream全文はrun log側である。

PowerShellからの起動例:

```powershell
$request = @{
  agent = "cursor"
  prompt = "Reply with only the word pong."
  working_directory = "D:\work\repo"
  model = "composer-2.5"
  fast = $false
  timeout_seconds = 600
} | ConvertTo-Json -Compress
$request | & "D:\Tools\Development\CodexAgentFacade\CodexAgentFacadeCli.exe"
```

Cursor で `reasoning_effort` または `fast` を指定すると、起動直前の `cursor-agent models` から基底・effort・fast に一致する識別子を1つ選んで `--model` に渡す。`composer-2.5` に effort の兄弟は無く、`fast=$false` は `composer-2.5`、`fast=$true` は `composer-2.5-fast` になる。両方を省略した model 文字列はそのまま渡す。一致が無ければ agent は起動しない。

呼び出し側は標準出力のJSONだけを結果として読む。`exit code` が0でも、作業の採否は `result.outputText` と成果物を見て決める。

## プロセス寿命と同期

CLIプロセスが1件の実行である。呼び出し側はprocessを起動し、stdinを書き、stdoutのJSONと終了コードを待つ。MCPのような `jobId`、`wait_agent_job`、`get_agent_job` は無い。

実行中の逐次出力はstdoutへ流さない。同じrun log（`runs\{runId}.log` と `.events.jsonl`）へ書く。`CODEX_AGENT_FACADE_LOG_DIR` が空でなければ、hostと同じくそのディレクトリをrun logに使う。viewer設定とhostの `server.log`、CLIの `cli.{processId}.log` は `%USERPROFILE%\.codex-agent-facade\` に置く。ユーザー設定で run log viewer が有効なら、MCP jobと同じくWindows Terminalが開く。viewerの失敗ではagent実行を失敗にしない。`timeout_seconds` で止めたとき、run logのfacadeイベントは実行層の `cancelled` であり、stdoutの `status` は `timed_out` である。

子agentはWindows Job Object（`KILL_ON_JOB_CLOSE`）に入る。CLIプロセスが終了すると子process treeも終了する。常駐MCPホストの中では、この寿命はhost process全体に結び付く。CLIでは呼び出し1回に結び付く。

## 取消と時間制限

| 操作 | 動作 | 終了コード | status |
| --- | --- | --- | --- |
| 正常完了 | JSON resultを返す | 0 | `completed` |
| agentまたはDriverの失敗 | JSONの `failure` を返す。別agentへは切り替えない | 1 | `failed` |
| JSON、未知フィールド、非対応のmodel/effort/fast、未知agent | agentを起動しないか、起動前に止める | 2 | `failed`、`failure.kind=invalid_request` |
| Ctrl+C、または読み取り中の取消 | 子process treeを止め、JSONを返す | 3 | `cancelled` |
| `timeout_seconds` の期限 | 子process treeを止め、JSONを返す | 4 | `timed_out` |
| CLIプロセスの強制終了 | 終了処理が走る前ならJSONは出ない。Job Objectが子を止める | プロセスの強制終了コード | 無し |

MCPの `wait_agent_job` timeoutはworkerを止めず `running` を返す。CLIの `timeout_seconds` は逆で、期限が来たらその実行を止める。通常アプリはprocessの完了を結果取得とみなすため、期限後も裏でagentを残さない。数値の上限1〜86400はwaitの引数範囲と揃えているが、意味は違う。

Ctrl+Cではコンソール制御を握り、結果JSONを書いてから終了する。stdin/stdoutをリダイレクトした親がプロセスを `Kill` した場合は、JSONを書けないことがある。停止自体は子processの終了で完了する。

## 終了コード

終了コードは上表のとおり固定する。成功と失敗の詳細はstdoutのJSONを読む。標準エラーの有無では判断しない。

## session継続と重複起動

継続は `session_id` だけである。前回の `working_directory`、model、effort、fastは継承しない。MCPと同じく毎回すべての指定を書く。

CLIは `%USERPROFILE%\.codex-agent-facade\jobs\` を読み書きしない。したがって次は提供しない。

- `request_id` によるExact retry
- 保存済みjobの再取得
- 同じrequestの二重起動防止
- Facade再起動後のfail-closed

同じ結果を取り損ねた場合、終了済みCLIを再実行するとagentはもう一度動く。会話の続きが必要なら、前回stdoutの `result.sessionId` を次の `session_id` に渡す。stdoutを失った場合のsession IDは、対応するrun logから読む。

同一 `session_id` のCLIを重ねて起動する排他はしない。呼び出し側が同時に同じ外部sessionを使わない。別プロセスの別sessionは、MCPホスト内の並行jobとは独立に、起動した数だけ同時実行できる。

## 環境とデスクトップ

CLIは呼び出し側の環境変数とWindowsセッションを継承する。interactive desktopで動く通常アプリから起動すれば、子agentもそのユーザーセッションで動く。session 0やWindows Serviceからは起動しない。Facadeはセッションを作らない。

認証tokenをJSONフィールドとしては受け取らない。GitHub Copilot CLIが `COPILOT_GITHUB_TOKEN` を見る場合、呼び出し側がCLIプロセスの環境変数として渡せば子へ継承される。FacadeのDriverはその変数を追加しない。各CLIの既存ログインも、PATH上の実行ファイルからそのまま使う。

## 既存MCP経路

`start_agent`、`wait_agent_job`、`get_agent_job`、`cancel_agent_job`、request fingerprint、保存済みjob、Exact retryは `AgentJobService` のままである。CLIは `AgentFacade.RunAsync` だけを共有する。model、reasoning effort、fastの非対応と競合は、従来どおり黙って無視せず起動前またはDriverの失敗として返す。

## FlaUIとの比較

比較対象は `tool_flaui_run_and_generate` の main `362590f119e2b994b2fa98147c4bf07d11b2efe9` にある Workbench agent runnerである。この文書はFacade CLIの契約を固定する。FlaUIアプリ本体の呼び出し先変更は、別の変更と検証が必要であり、ここでは行わない。

| 項目 | FlaUI現行 | Facade CLI | 後続移行 |
| --- | --- | --- | --- |
| 起動対象 | providerごとの `ExecutablePath` と `ArgumentsTemplate` | Facade CLI 1本。provider引数はDriver | FlaUIのテンプレート置換をやめる変更が必要 |
| 入力 | prompt本文を子プロセスのstdinへ渡す | promptをJSONの `prompt` に入れる。FacadeがDriverごとのstdinまたは引数へ渡す | 呼び出し側はJSONを作る |
| model / effort | `{model}` と `{reasoning_effort}` の文字列置換。既定は `gpt-5.6-terra` / `high` | 構造化フィールド。非対応は失敗。自動選択はしない | 設定値をそのまま渡せる。テンプレート構文は不要 |
| fast | `WorkbenchAgentOptions` に項目が無い | `fast` を渡せる。CopilotとGrokは明示値を拒否する | 使うならFlaUI設定への追加が別途必要 |
| 認証 | Copilot runnerが `COPILOT_GITHUB_TOKEN` を必須注入する。CodexはローカルCLIログイン | JSONにtoken欄は無い。プロセス環境は継承する | Copilotを使うなら、FlaUIがCLIプロセスへ同じ環境変数を入れる |
| interactive session | `UseShellExecute=false` でWorkbenchと同じセッション。`CreateNoWindow=true` | 呼び出し元セッションを継承するコンソールexe | Workbenchプロセスから起動すれば同じセッションになる。常駐MCPホスト経由にはしない |
| 環境変数 | requestと追加tokenを子へ渡す | CLIプロセスの環境を継承する。JSONの環境変数mapは無い | 必要な変数は起動時のProcessStartInfoで渡す |
| progress | stdout/stderrの行を `IProgress` で実行中に返す | 実行中のstdoutは最終JSONだけ。行のpushは無い | UIの逐次表示は run log の追従か、後続の行プロトコルが必要。今回は不足のまま |
| artifact | 生成ファイルの探索、status reconciliation、出力ディレクトリをWorkbenchが持つ | `outputText`、`sessionId`、run log pathを返す。成果物ディレクトリは走査しない | 成果物判定はFlaUIに残る |
| timeout | 既定600秒。期限でprocess treeを殺し、status `Timeout` | 省略時は無制限。指定時は殺して `timed_out` / exit 4 | FlaUIの600秒を `timeout_seconds` に明示する |
| cancel | CancellationTokenでprocess treeを殺し、status `Cancelled` | 親がCLIを止める。Ctrl+CならJSONの `cancelled`。強制終了はJSONなしで子も止まる | 親のcancelをCLIプロセスの終了に対応付ける |
| result | command、stdout、stderr、exit code、時刻、status、summary。生成物の成功は別reconciliation | `completed` と公開result、または `failure`。stdout全文は返さない | 既存のstdout解析を、このJSONと成果物探索へ置き換える必要がある |
| session継続 | runner契約にsession IDは無い | `session_id` で継続できる | 現行の単発実行は省略で足りる |
| 重複起動防止 | Workbenchの進行管理に依存 | CLIはrequestの重複を記憶しない | アプリが同時実行を抑止する。MCPのjob storeは使わない |

後続でFlaUIを移すときの最小接続は、provider別の引数テンプレートの代わりに、このJSONを `CodexAgentFacadeCli.exe` へ渡し、終了コードと `status` / `result` / `failure` を読み、成果物探索は現行のまま残すことである。progressの行pushと、Copilot tokenの環境変数注入は、その移行依頼で個別に扱う。
