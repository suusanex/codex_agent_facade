---
name: adaptive-implementation-execution
description: Use only when the user explicitly invokes this skill with /adaptive-implementation-execution. Do not select for ordinary implement-this-plan requests such as "実装して" or "このPlanを実装して", and do not select from natural-language mentions such as "Adaptive Implementationを使って".
user-invocable: true
disable-model-invocation: true
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: CC-BY-4.0
# License: https://creativecommons.org/licenses/by/4.0/
# Source: https://github.com/suusanex/coding_agent_plan_and_verify_process
---

# Adaptive Implementation Execution

この skill は、利用者が `/adaptive-implementation-execution` で明示起動した場合だけ使用する。通常の「実装して」「このPlanを実装して」などの実装依頼、および「Adaptive Implementationを使って」などの自然文での名前言及だけでは選択しない。package が導入されているだけで、repository 内の実装作業へ自動適用しない。

この skill が明示起動された後、通常の Plan Mode output、手書き Plan、repository-tracked Plan、Issue 内の実装計画、または明示選択された Design Pair Implementation Handoff を入力に、実装中の evidence に基づいて HIGH_MODEL と STANDARD_MODEL を直列に切り替える implementation-only flow です。

Plan Coverage Lite / Standard / Full Coverage の縮小版ではありません。Plan Coverage artifacts、change-risk-triage、runtime contract、test design、coverage ledger、residual decision は必須入力にしません。

## Parent role

parent / router は次だけを担当します。

- input の source of truth と最低限の Implementation Intent を確認する
- agent を直列に起動する
- verdict と handoff contract を検証する
- re-entry 時に元 intent と handoff を保持する
- 最終状態と未検証事項を集約する

parent / router は production code や tests を横取りして直接実装しません。write-heavy agent を並列に起動しません。

## Accepted inputs

- Codex 等の通常 Plan Mode output
- repository-tracked Plan file
- caller が直接渡した短い実装計画
- Issue / prompt 内の goal、scope、acceptance、constraints
- `plans/<slug>-design-pair-implementation-handoff.md` の Design Pair Implementation Handoff（Design Pair route が明示選択された場合だけ）

内部では必要な項目だけを次の形で解釈します。

```yaml
implementation_intent:
  goal:
  scope:
  non_goals:
  acceptance:
  constraints:
  validation:
  plan_reference:
```

`goal`、`scope`、`acceptance` は必須です。`non_goals`、`constraints`、`validation`、`plan_reference` は任意です。未指定時は次のように扱います。

- `non_goals`: source request から明確に導ける場合だけ記録し、それ以外は `Not specified` とする。existing code から scope を狭めない
- `constraints`: user request または repository instructions が強制する内容だけを記録する
- `validation`: repository standard から推定できる
- `plan_reference`: source request から特定できる場合だけ記録する

長い正規化 artifact を常に作成しません。最低限、何を変更するか、scope、完了条件を判断できれば inline intent のまま進めます。

入力不足によりこの3点を判断できない場合は、内部設計を推測せず `REPLAN_REQUIRED` または `HUMAN_DECISION_REQUIRED` で停止します。

任意 template は `refs/intent.md` です。

## Optional Design Pair input

Design Pair Implementation Handoff が入力にある場合、次を検証します。

- `implementation_route: design-pair`
- `implementation_route_source: explicit-user-selection`
- verdict が `READY_FOR_ADAPTIVE_IMPLEMENTATION`
- `Interaction stage: complete`
- original Plan / Implementation Intent reference、Target Map、`Upstream Binding Constraints`、Locked Decisions、readiness check が存在する
- Target Map presentation evidence と Target 選択要求 evidence がある
- Target Map presentation evidenceが、全Targetのuser-facingな具体的file / symbol、current invariant、内部設計判断候補、relevant evidenceを参照し、artifact linkまたは論点名だけの要約ではない
- Target Map 提示後の actual user response reference と `User response occurred after Target Map presentation: Yes` がある
- 一件以上の selected Target、または explicit all-Adaptive delegation があり、pending human-owned Target がない
- Target Map ID が一意で、summary の全 Target ID が Target Map に実在する
- `Selected`、`Delegated-to-Adaptive`、`No-Change`、`Upstream-Decision-Required`、`Pending human-owned` の5集合が互いに素で、和集合が Target Map 全体と完全一致する
- 各 summary 集合が Target Map row の `Locked` / `Discussed-Unlocked`、`Adaptive-Owned`、`No-Change`、`Upstream-Decision-Required`、pending disposition と一致する
- blocking な `Upstream-Decision-Required` がない
- `Locked Decisions` の各 entry に Design Pair Decision ID、Target ID、actual user message / turn reference、confirmed content、post-map confirmation `Yes` がある
- 各 Locked Decision Target ID が `Selected Target IDs` に含まれ、その Target Map row が `Locked` である
- `Selected Target IDs`と`Delegated-to-Adaptive Target IDs`の各Targetに一件だけ`Target Disposition Evidence`があり、Target Map rowと一致する`Locked` / `Discussed-Unlocked` / `Adaptive-Owned`、actual user message / turn reference、confirmed content、post-map confirmation `Yes`を持つ
- selected Targetごとに`Selected Target Discussion Evidence`があり、user-facing assistant turn reference、具体的code location、current invariant、alternatives / trade-offs、proposalまたはNo proposal理由、validation expectationを含む
- explicit all-Adaptive delegation では selected / pending が `None`、Locked Decisions がなく、全 Target row が `Adaptive-Owned` で delegated集合と完全一致する

Design Pair が今回新たに作る decision のうち binding なのは `Locked Decisions` に明示された事項だけです。original Plan / Implementation Intent、repository policy、および handoff の `Upstream Binding Constraints` は Design Pair Decision ID を持たない既存の binding input として別に守ります。Target Map、`Upstream User Initial Positions`、`Discussed but Unlocked`、`Adaptive-Owned`、Known Evidence、Known Assumptions、Knowledge Candidates は参考情報であり、HIGH_MODEL の通常 authority を拘束しません。

上の条件が欠落または矛盾する場合、Target 未選択を空集合として PASS にせず、架空 ID、重複 ID、未分類 Target、row / summary 不一致、Target Disposition Evidenceの欠落・重複・架空ID・row disposition不一致・pre-map reference、抽象的な論点名だけで具体的なSelected Target discussion evidenceがないartifactも拒否して HIGH_MODELを起動しません。AIが未選択Targetを自己判断で`Adaptive-Owned`へ移したartifact、または利用者の最終応答なしに`Discussed-Unlocked`へ移したartifactも拒否します。`BLOCKED` / `BlockedByInvalidCompletionHandoff`としてraw observed fields、`<missing>`、artifact repair evidenceを返し、upstream textからuser responseを再構成したりAdaptiveへfallbackしたりしません。

Design Pair handoff の `Affected files / symbols` と Target Map の file / symbol は Decision の適用対象または調査 evidence であり、Adaptive Implementation の `Allowed edit surface` として扱いません。HIGH_MODEL は goal、scope、acceptance を満たすために必要な関連 code / tests / production wiring を通常どおり調査・編集できます。

Design Pair handoff がない場合は、新規 intake と resume を分けます。durable route artifact、既存 implementation handoff、resume request、Design Pair selection evidence が一切ない新規 intake だけ、次の metadata を初期化します。

```yaml
implementation_route: adaptive
implementation_route_source: default
design_pair_handoff: N/A
```

resume または既存 artifact がある状態では `implementation_route`、`implementation_route_source`、`design_pair_handoff` を durable state から読み、欠落や矛盾を Adaptive へ補完しません。Design Pair selection、Decision ID、Target Map、または Design Pair handoff path の evidence があるのに route metadata / handoff が欠ける場合は `BLOCKED` とし、missing fields、evidence、必要な artifact repair を報告します。

唯一の互換例外は、Design Pair 導入前の `Implementation Completion Handoff` schema と一致し、Design Pair evidence が一切ない tracked handoff の resume です。この場合は `Legacy Adaptive handoff normalization` を適用し、`implementation_route: adaptive`、`implementation_route_source: default`、`design_pair_handoff: N/A`、`route_metadata_normalization: legacy-adaptive-handoff` を記録してから続行できます。部分的に新schemaを持つ handoff、Design Pair evidence がある handoff、旧schemaの必須fieldが欠ける handoffにはこの例外を適用しません。

Design Pair route の場合も HIGH_MODEL は通常の adaptive implementation と同じ authority を維持し、original Plan / upstream constraints と Locked Decisions を守ったうえで、それ以外の新しい decision surface を実コードと verification evidence に基づいて処理します。

## Required execution order

```text
ordinary Plan / short implementation intent
  or explicit Design Pair Implementation Handoff
  -> high-implementation-starter [HIGH_MODEL]
       -> READY_FOR_STANDARD_COMPLETION
            -> standard-implementation-completer [STANDARD_MODEL]
                 -> COMPLETED
                 -> NEEDS_HIGH_MODEL_REENTRY
                      -> high-implementation-starter [HIGH_MODEL]
       -> CONTINUE_HIGH_IMPLEMENTATION
       -> COMPLETED_BY_HIGH_MODEL
       -> REPLAN_REQUIRED
       -> HUMAN_DECISION_REQUIRED
       -> BLOCKED
```

すべての非自明な implementation は `high-implementation-starter` から開始します。課題全体が small-bounded、low risk、少数ファイルであることだけを理由に STANDARD_MODEL へ直行してはいけません。

## Runtime adapters

- Codex: `high-implementation-starter` / `standard-implementation-completer`のportable contractを使い、concrete modelはrepository-local `.codex/agents/*.toml`で補完できる。
- GitHub Copilot Chat in VS Code: `high-implementation-starter`は`GPT-5.6 Terra (copilot)`、`standard-implementation-completer`は`GPT-5.6 Luna (copilot)`、re-entryは再びTerraを要求する。

Copilotのhandoff buttonは手動遷移候補であり、verdictを検証するrouterではありません。HIGHからSTANDARDへのbuttonはvalidな`READY_FOR_STANDARD_COMPLETION`でだけ使い、`COMPLETED_BY_HIGH_MODEL`とstop verdictでは次agentを起動しません。STANDARDからHIGHへのbuttonはvalidな`NEEDS_HIGH_MODEL_REENTRY`でだけ使います。Copilotのagent/model遷移ではtracked handoffを必須とし、会話履歴だけを唯一のstate保持手段にしません。

## Step 1: Validate the intent

1. repository instructions と user constraints を確認する。
2. goal、scope、acceptance を抽出する。constraints、non-goals、validation expectation、Plan reference があれば併せて抽出する。
3. missing information が implementation detail か、product / scope / acceptance decision かを分ける。
4. product / scope / acceptance が不足する場合は実装を開始しない。
5. Design Pair handoff がある場合は route metadata、readiness、blocking upstream decision、explicit Locked Decision entries を検証する。

validation expectation が明示されていない場合は repository standard を採用し、agent input と最終出力に `Validation expectation: inferred from repository` と記録します。

Plan Coverage artifacts が存在しないことは blocker ではありません。caller が binding artifact として明示した場合だけ追加 input として渡します。

Plan Coverage、Behavior Case、slice、runtime-contract、test-point、implementation-contract、または gap binding artifact が supplied input に含まれる場合は、各 implementation owner に canonical agent contract の `Implementation Self-Map Delta` を返させます。orchestrator は phase ごとの delta を stable Change ID で `plans/<slug>-implementation-execution.md` の canonical `Implementation Self-Map` に集約します。binding artifacts がない standalone run では、この traceability extension は evidence-backed `N/A` でよく、Plan Coverage artifact を新規要求しません。

## Step 2: Start with HIGH_MODEL

`high-implementation-starter` custom agent / subagent を一度だけ起動し、完了するまで待ちます。

渡すもの:

- original Plan または Implementation Intent
- `implementation_route`
- `implementation_route_source`
- repository instructions
- current worktree status
- relevant source pointers already known
- validation expectations
- previous Implementation Completion Handoff と High-model Re-entry Handoff（STANDARD_MODELからresumeする場合）
- Design Pair Implementation Handoff path（`adaptive / default`では明示的な`N/A`、`design-pair / explicit-user-selection`ではcurrent tracked path）
- Design Pair Decision IDs（存在する場合）

parentはHIGH_MODEL起動前に、route pairが`adaptive / default`または`design-pair / explicit-user-selection`のどちらかであり、Design Pair evidenceおよびhandoff pathと一致することを検証します。`adaptive / default`ではpathが明示的な`N/A`、`design-pair / explicit-user-selection`ではcurrent tracked pathであることを要求します。fieldの欠落、組み合わせ矛盾、またはevidenceとの不一致がある場合はHIGH_MODELを起動せず`BLOCKED`で停止し、`Stop reason: BlockedByInvalidCompletionHandoff`とraw observed values、欠落fieldの`<missing>`、artifact repairに必要なevidenceを報告します。

HIGH_MODELはactual code、production wiring、signatures、call sites、testsを読み、非局所decisionを閉じます。decision closureに必要な場合だけ最小の自然なproduction/test変更とfocused verificationを行い、code inspectionだけで閉じられる場合はcodeを変更せずSTANDARDへ渡せます。事前文書だけでrouteを固定分類しません。

## Step 3: Validate the HIGH_MODEL verdict

通常はすべてのHIGH_MODEL resultについて、`implementation_route`、`implementation_route_source`、Design Pair handoff pathまたは`N/A`が存在し、incoming durable route identityと完全一致することを検証します。唯一の例外は`Verdict: BLOCKED`かつ`Stop reason: BlockedByInvalidCompletionHandoff`のresultです。この場合は完全なroute pairを要求せず、各fieldのraw observed valueまたは欠落を示す`<missing>`とartifact repair evidenceを要求して受理し、追加実装や委譲を行わず停止します。その他のresultで欠落、変更、不一致、またはDesign Pair evidenceとの矛盾がある場合は受理せず、同じinvalid-artifact `BLOCKED`として停止します。

### COMPLETED_BY_HIGH_MODEL

HIGH_MODELがscope内のacceptance itemをすべて`Complete`とし、各itemの実装またはvalidation evidence、checks、remaining uncertainty、検証済みroute identity、`Direct completion reason`と具体的根拠を報告した場合に受理します。許可reasonは`tiny-local-change`、`design-implementation-inseparable`、`standard-model-unavailable`、`delegation-materially-increases-risk-or-cost`、`post-reentry-high-ownership`だけです。`post-reentry-high-ownership`は実際のre-entry後だけ、`tiny-local-change`は独立したmeaningfulなWork Packageが残らない場合だけ受理します。初回HIGH completionにreasonがない場合はverdictを拒否し、委譲または有効なexception evidenceを求めます。

### CONTINUE_HIGH_IMPLEMENTATION

同一 run で継続可能なら agent にそのまま続行させます。parent へ細かく返して再起動しません。resume、別 run、または execution boundary が必要な場合だけ state verdict として受理します。

### READY_FOR_STANDARD_COMPLETION

`refs/handoff.md` の必須 field がすべて存在し、次を満たす場合だけ受理します。

- Original Implementation Intentのtracked pathまたはgoal / scope / acceptance / constraints / validation snapshotがある
- `Delegation basis: non-local-decisions-closed`である
- `HIGH_MODEL code changes: Yes / No`が明示されている
- Decision closureのresponsibility / ownership、public / shared internal contract、dependency direction、production sequence / wiring architecture、state / error / cancellation / retry semantics、test architecture / seam strategyがすべて`Locked`または理由付き`N/A`であり、`Unresolved`がない
- Applicability evidenceがDecision closureをactual code、wiring、signatures、call sites、existing testsへ接続している
- HIGHがcodeを変更した場合は関連checkと自然なworktree state、変更していない場合はinspection evidenceとbuild/testをWork Packageへ委ねた理由がある
- scope 内の全 acceptance item と現在の status / evidence が列挙されている
- `Blocked` の acceptance item が存在しない
- すべての `Incomplete` acceptance item が1件以上の `Remaining work` Work ID に対応している
- すべての `Remaining work` row が1件以上の `Incomplete` acceptance item に対応している
- すべての `Complete` acceptance item に implementation または validation evidence がある
- Acceptance status の mapping と Remaining work の acceptance item(s) が双方向に一致している
- locked decisions が明示されている
- Design Pair 由来の Locked Decisions が origin と Design Pair Decision ID を保ったまま、HIGH_MODEL が実装中に確定した decisions と統合されている
- Design Pair handoff path または `N/A` が明示されている
- remaining workがWork ID、Acceptance item(s)、Responsibility、Authorized surface、Expected behavior、Locked boundaries、Local freedom、Completion checkを持つWork Packageである
- Allowed edit surfaceが全Work PackageのAuthorized surfaceを包含するdirectory / file group envelopeとして明示されている
- high-model re-entry triggers が明示されている
- 残る不確実性がlocked boundaryを変更しないlocalかつreversibleなimplementation choiceだけである

不足がある場合は STANDARD_MODEL へ渡さず、HIGH_MODEL に handoff 修正または実装継続を求めます。

Design Pair導入前に作成されたtracked handoffのresumeでは、`refs/handoff.md`の`Legacy Adaptive handoff normalization`を先に適用します。exact legacy handoffは旧来の狭いauthorizationを維持し、0.5 fieldsを推測しません。normalization recordとdeterministic legacy Decision IDsをparent / routerがtracked handoffに追記してからSTANDARD_MODELへ渡します。0.5 fieldsを欠く0.4系current-schema handoffはlegacy扱いせず、HIGH_MODELによるhandoff再発行を要求します。

### Stop verdicts

`REPLAN_REQUIRED`、`HUMAN_DECISION_REQUIRED`、`BLOCKED` は理由、既実装、worktree state、次に必要な input を保持して停止します。

Locked Decision conflict で停止する場合は、少なくとも affected Design Pair Decision ID、actual code / production wiring / dependency evidence、Decision を維持できない理由、files changed、current worktree state、checks performed、利用者が次に判断すべき事項を報告します。Locked Decision を黙って変更せず、automatic Design Pair re-entry は行いません。

## Step 4: Delegate bounded completion

`READY_FOR_STANDARD_COMPLETION` のときだけ `standard-implementation-completer` を起動します。GitHub Copilot Chat in VS CodeではTerraからLunaへの別model / agent遷移になるため、Implementation Completion Handoffを必ず`tracked`で保存し、そのpathをhandoff promptへ渡します。

渡すもの:

- original Plan / Implementation Intent
- complete Implementation Completion Handoff
- current diff / worktree status
- repository instructions
- Design Pair Decision IDs を含む統合済み Locked decisions

HIGH_MODELとSTANDARD_MODELを同時に起動しません。STANDARD_MODELはWork PackagesとAllowed edit surface envelope内のproduction implementation、tests、validationを主体として担当し、locked boundariesを変更しないlocalかつreversibleなimplementation choiceを自律判断できます。private helperとinline、branch順序、既存utility、fixture構成などに複数の局所案があること自体はre-entry理由ではありません。

## Step 5: Handle STANDARD_MODEL result

通常はすべてのSTANDARD_MODEL resultについて、`implementation_route`、`implementation_route_source`、Design Pair handoff pathまたは`N/A`が存在し、incoming Implementation Completion Handoffと完全一致することを検証します。唯一の例外は`Verdict: BLOCKED`かつ`Stop reason: BlockedByInvalidCompletionHandoff`のresultです。この場合は完全なroute pairを要求せず、各fieldのraw observed valueまたは欠落を示す`<missing>`とartifact repair evidenceを要求して受理し、追加実装やre-entryを行わず停止します。その他のresultで欠落、変更、不一致、またはDesign Pair evidenceとの矛盾がある場合は受理せず、同じinvalid-artifact `BLOCKED`として停止します。

### COMPLETED

completion scope、validation results、Design Pair Decision ID ごとの locked-decision compliance、検証済みroute identityに加え、scope 内の全 acceptance item が `Complete` で evidence を持つことを確認します。未完了 item があれば `COMPLETED` を受理しません。これは implementation completion であり final review 完了ではありません。

### NEEDS_HIGH_MODEL_REENTRY

`NEEDS_HIGH_MODEL_REENTRY`は、有効なImplementation Completion Handoffのauthorization後、許可された実装または検証の途中でlocked non-local decisionを変更する必要が判明した場合だけ受理します。新規file、locked済みclass/interfaceの作成、または決定済みwiringの実装という編集種別だけでは受理しません。STANDARD_MODELのtracked `High-model Re-entry Handoff`、元のtracked `Implementation Completion Handoff`、元のImplementation Intent、元のlocked decisions、current worktree stateを保持して`high-implementation-starter`を直列に再実行します。両handoffの`implementation_route`、`implementation_route_source`、Design Pair handoff pathが一致することを再実行前に検証し、欠落または不一致があればHIGH_MODELを起動せず`BLOCKED`で停止し、`Stop reason: BlockedByInvalidCompletionHandoff`を報告します。Copilotでは両artifact pathをTerraへのhandoff promptに渡します。

STANDARD_MODEL に redesign を続行させません。re-entry 後の HIGH_MODEL は actual code と new evidence を読み、必要な設計判断と実装を行います。1 回 re-entry した後は HIGH_MODEL が完了まで担当することを既定とします。

re-entry state は次の順に更新します。

1. 初回 HIGH_MODEL handoff は `reentry_count: 0`、`previous_reentry_trigger: N/A`、`delegation_surface_reduced: N/A` とする。
2. STANDARD_MODEL は `NEEDS_HIGH_MODEL_REENTRY` で、`Trigger` に今回の trigger、`reentry_count` に incoming value + 1、`previous_reentry_trigger` に incoming value を設定する。
3. HIGH_MODEL が再委譲する場合、re-entry handoff の `reentry_count` を維持し、`previous_reentry_trigger` にその `Trigger` を設定し、`delegation_surface_reduced: Yes` とする。

再委譲できるのは、前回 handoff と比較して `Remaining work` と `Allowed edit surface` の両方が厳密に縮小し、re-entry handoff の `Trigger` がその `previous_reentry_trigger` と異なり、`delegation_surface_reduced: Yes` を evidence 付きで記録できる場合だけです。それ以外は HIGH_MODEL が実装を継続します。

### Other stop verdicts

`REPLAN_REQUIRED`、`HUMAN_DECISION_REQUIRED`、`BLOCKED` は変更内容と blocker を保持して停止します。Design Pair Locked Decision conflict が原因の場合は Decision ID と conflict evidence を保持します。

## Handoff persistence

- `inline`: 同一 run / 同一 parent orchestration / 同一model内の通常 handoff。Codexの同一parent orchestrationでは既定値にできる。
- `tracked`: resume、別 thread、別 model、別agent、別作業者へ渡す場合。GitHub Copilot Chat in VS CodeのHIGH -> STANDARDとSTANDARD -> HIGHでは必須。

tracked completion handoff の推奨 path は `plans/<slug>-implementation-completion-handoff.md`、re-entry handoffは`plans/<slug>-high-model-reentry-handoff.md`です。実コードを source of truth としつつ、Original Implementation Intent、route identity、Design Pair handoff path / Decision IDs、Locked Decisions、current worktree stateをmodel間で失わないだけのdurable stateを保持します。

## Verification boundary

各 implementation agent に、変更へ関連する build、focused test、lint、format、type check を可能な範囲で要求します。実行できない check は理由と未検証範囲を明記します。

この skill は final code review、総合 architecture review、human review、独立 verification の代替ではありません。最終出力には必ず `Final review status: Not performed by this flow` または、caller が別工程で実施した actual status を記録します。

## Final output

- source Plan / Implementation Intent
- implementation route metadata
- Design Pair tracked handoff pathまたは`N/A`、Target Map reference、Locked Decision IDs（後二者はDesign Pair routeの場合）
- route taken
- agent verdict sequence
- implementation owner by phase
- files changed
- validation performed and results
- acceptance status table with evidence for every in-scope item
- tracked handoff path, if any
- re-entry events, if any
- Locked Decision compliance evidence と conflict の有無（Design Pair route の場合）
- remaining work / human-required work / blockers
- final review status
