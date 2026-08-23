# Implementation Intent

この template は、通常 Plan または Issue 内の実装計画から最小限の実装意図を補う必要がある場合だけ使用する。十分な入力がある場合は inline のまま進め、artifact を新設しない。

```yaml
implementation_intent:
  goal:                    # required
  scope:                   # required
  acceptance:              # required
  non_goals:               # optional; Not specified unless clear in source request
  constraints:             # optional; only enforced user/repository constraints
  validation:              # optional / inferable from repository
  plan_reference:          # optional / inferable from source request
```

## Readiness check

- What changes is clear: Yes / No
- Scope boundary is clear: Yes / No
- Completion condition is clear: Yes / No
- Validation expectation: explicit / inferred from repository
- Missing product or policy decision:
- Result: READY / REPLAN_REQUIRED / HUMAN_DECISION_REQUIRED
