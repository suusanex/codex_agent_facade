# Implementation Completion Handoff

- Verdict: READY_FOR_STANDARD_COMPLETION
- Handoff persistence: inline / tracked
- Original Implementation Intent: <tracked path or goal / scope / acceptance / constraints / validation snapshot>
- Plan reference:
- implementation_route: adaptive / design-pair
- implementation_route_source: default / explicit-user-selection
- Design Pair handoff: N/A / plans/<slug>-design-pair-implementation-handoff.md
- Delegation basis: non-local-decisions-closed
- HIGH_MODEL code changes: Yes / No
- Validation performed:
- reentry_count: 0
- previous_reentry_trigger: N/A
- delegation_surface_reduced: N/A

初回 handoff は上記の初期値を使用する。`implementation_route` と `implementation_route_source` はincoming durable route pairを変更せず伝播し、`adaptive / default`または`design-pair / explicit-user-selection`の組み合わせだけを許可する。current-schema handoffで片方が欠ける、矛盾する、またはDesign Pair evidenceと一致しない場合は`BLOCKED` / `BlockedByInvalidCompletionHandoff`として停止し、下記のlegacy normalizationで補完しない。re-entry 後に再委譲する場合は、re-entry handoff の `reentry_count` を維持し、`previous_reentry_trigger` にその `Trigger` を設定し、`delegation_surface_reduced: Yes` とする。GitHub Copilot Chat in VS CodeのTerra -> Luna -> Terra遷移では必ず`tracked`を使用し、Original Implementation Intentと両handoff pathをpromptに渡す。会話履歴だけをdurable stateにしない。

## Acceptance status

| Acceptance item | Status | Evidence | Remaining work mapping (Work ID) |
| --- | --- | --- | --- |
| | Complete / Incomplete | | |

`READY_FOR_STANDARD_COMPLETION` では `Blocked` を許可しない。blocked item がある場合は handoff を作らず、適切な stop verdict を返す。すべての `Incomplete` item は1件以上の Work ID に対応し、すべての Remaining work row は1件以上の `Incomplete` item に対応する。`Complete` item には implementation または validation evidence を記録する。

## Decision closure

| Concern | Status | Locked decision / evidence or N/A reason |
| --- | --- | --- |
| Responsibility / ownership | Locked / N/A | |
| Public / shared internal contract | Locked / N/A | |
| Dependency direction | Locked / N/A | |
| Production sequence / wiring architecture | Locked / N/A | |
| State / error / cancellation / retry semantics | Locked / N/A | |
| Test architecture / seam strategy | Locked / N/A | |

`Unresolved`が1件でもあるhandoffは`READY_FOR_STANDARD_COMPLETION`として受理しない。`N/A`には適用外である具体的理由を記録する。

## Applicability evidence

| Concern | Applicability | Evidence or N/A reason |
| --- | --- | --- |
| Production path and wiring | Applicable / N/A | |
| Test harness | Applicable / N/A | |
| Test seam | Applicable / N/A | |
| Mock boundary | Applicable / N/A | |

このsectionはimplementation complete evidenceではなく、上記Decision closureがactual code、wiring、signatures、call sites、existing testsへ適用できる根拠を記録する。

## Implemented

- 実装済みの production code、tests、wiring、代表経路
- `HIGH_MODEL code changes: No`の場合は`None (inspection-only)`とし、decision closureを支えるinspection evidenceをApplicability evidenceへ記録する

## Locked decisions

| Origin | Decision ID | Decision | Affected files / symbols | Validation expectation | Compliance evidence |
| --- | --- | --- | --- | --- | --- |
| Design Pair / HIGH_MODEL | DP-D01 / HIGH-D01 | | | | Pending / evidence |

Design Pair 由来の entry は origin と Design Pair Decision ID を維持する。HIGH_MODEL が実装中に確定した decision は別 ID で追加する。`Affected files / symbols` は decision の適用範囲であり、Allowed edit surface ではない。

## Remaining work

| Work ID | Acceptance item(s) | Responsibility | Authorized surface | Expected behavior | Locked boundaries | Local freedom | Completion check |
| --- | --- | --- | --- | --- | --- | --- | --- |
| RW-1 | | | | | | | |

## Allowed edit surface

| Directory / file group | Allowed change envelope |
| --- | --- |
| | |

Allowed edit surfaceは全Work PackageのAuthorized surfaceを包含する編集許可envelopeである。directory / file groupを使用でき、Locked boundariesを変えないprivate helperやfile-local typeの追加は個別symbol追記なしで許可できる。

## Validation commands

```text
<build / focused test / lint / format / type check>
```

## High-model re-entry triggers

- locked済み責務、配置、signatureでは成立せず、新しいshared abstraction / contract / dependency decisionが必要
- locked decision、public / shared internal API、schema、config surface の変更が必要
- locked済みDI lifetime / locationまたはproduction wiring architectureの変更が必要
- state ownership / error / cancellation / retry semanticsの変更が必要
- test architecture / seam strategy / harness方針の変更が必要
- allowed edit surface envelope外へのresponsibility移動が必要
- 複数案からの選択によってlocked non-local decisionを新設または変更する必要がある
- Plan と actual code が矛盾する

## Known assumptions / unresolved observations

-

## Design Pair Decision compliance

| Design Pair Decision ID | Status | Implementation evidence | Validation evidence | Conflict |
| --- | --- | --- | --- | --- |
| DP-D01 | Compliant / Conflict / Pending | | | None / evidence |

## Review boundary

- Final code review performed: No
- Independent verification performed: No

## Legacy Adaptive handoff normalization

Design Pair導入前のtracked handoffをresumeする場合は、旧schemaの必須fieldがすべて存在し、`Design Pair handoff`、`Design Pair Decision compliance`、Origin / Decision ID columnsがすべて欠け、Design Pair evidenceが一切ないことを確認する。条件を満たす場合だけ、次をtracked artifactへ追記してからlegacy-compatible authorizationとして検証する。

- `implementation_route: adaptive`
- `implementation_route_source: default`
- `route_metadata_normalization: legacy-adaptive-handoff`
- `Design Pair handoff: N/A`
- `Design Pair Decision compliance: N/A (legacy Adaptive handoff)`
- 旧Locked decisionsを出現順の`LEGACY-HIGH-D01`から始まるdeterministic IDで`Origin: HIGH_MODEL`へ正規化
- 欠けていたdecision columnsは`Affected files / symbols: Not specified in legacy handoff`、`Validation expectation: Inherit handoff validation commands`、`Compliance evidence: Pending legacy resume completion`とする

exact legacy handoffは旧来の狭いRemaining workとAllowed edit surface authorizationを維持する。0.5で追加した`Delegation basis`、`HIGH_MODEL code changes`、`Decision closure`、Work Package columnsを推測で生成しない。これらを欠く0.4系current-schema handoffはlegacy扱いせず、`BLOCKED / BlockedByInvalidCompletionHandoff`としてHIGH_MODELによる0.5 handoff再発行を要求する。

Design Pair selection、Decision ID、Target Map、handoff pathのevidenceがある場合、または新旧schemaが部分的に混在する場合はnormalizationせずfail closedにする。Affected files / symbolsの補完値はAllowed edit surfaceではない。
