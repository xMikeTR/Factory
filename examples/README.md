# Examples

Each one runs without a model, a network or a Jira.

| Folder | Shows | Try it |
|---|---|---|
| `pricing-v3/` | A handover and a valid plan. The handover carries `requirementRisk` and `requirementDataClass`, which the router prefers over its own guess | `dotnet run --project src/Factory.S2 -- validate --handover examples/pricing-v3/handover.json --plan examples/pricing-v3/plan.json` |
| `budget/` | Phase 4: a prices file, a spend file and the `budget:` policy | In a scratch repo: `factory-s2 budget-import --repo . --prices examples/budget/prices.csv --spend examples/budget/spend.csv`, then `factory-s2 budget --repo .` (copy `policy.yaml` to `.factory/policy.yaml` first) |
| `jira/` | A status snapshot for the one-way mirror | `factory-s2 jira-mirror --registry <registry> --snapshot examples/jira/snapshot.json` (add `--apply --comment` only with `JIRA_URL` and `JIRA_TOKEN` set: it posts gate evidence after each move; without `--apply` it prints the comment) |
