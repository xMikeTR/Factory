# Legacy parity on the work VM

The recorder runs on the work VM only, never in CI. Legacy is always live **dev** with real data, never mocks.
designc owns the comparison and the rules ([designc `docs/parity-contract.md`](../../../Designc/app/docs/parity-contract.md)).
S2 only runs designc's recorder for a work item and turns failures into decisions.

```mermaid
flowchart LR
  B["designc: --side legacy<br/>(once per scenario)"] -->|parity/baselines| N
  R["factory-s2 run<br/>(gates pass)"] --> N["factory-s2 parity<br/>--side next at HEAD"]
  N -->|parity_result rows| L[(ledger/pipeline.jsonl)]
  N -->|fail| D["needs_decision<br/>A like legacy / B ask PO"]
  L --> Done["item_done at that commit<br/>(done_when: parity)"]
```

## One-time setup

```sh
cd ~/Desktop/Coding/Designc/app   # wherever designc is checked out on the VM
npm i
npx playwright install chromium
export FACTORY_DESIGNC_DIR=$PWD
```

The two URLs are given on the work VM. S2 holds none. Today, pass them per run with `--base-url`: the legacy dev URL
to designc's recorder for baselines, and the new app's test environment to `factory-s2 parity`. designc also reads
`parity.legacy.baseUrl` / `parity.next.baseUrl` from the registry's `designc.json`, but leave them out: that file is
committed, and the new app's address belongs to whoever deploys it.

The new app's test environment is the only place where `--allow-writes` would ever be acceptable. S2 never passes
it, and nothing passes it against legacy.

## Baselines (designc, a person)

Record legacy once per scenario, and again when legacy changes:

```sh
node scripts/parity-record.js --registry <registry> --scenario all --side legacy --base-url <legacy dev URL>
```

S2 never writes `parity/scenarios/` or `parity/baselines/`.

## Per work item (after its gates pass)

1. Deploy the item's commit to the new app's test environment, or serve it locally and pass `--base-url`.
2. Run, from a clean checkout at that commit:

   ```sh
   factory-s2 parity --registry <registry> --item W-1 --repo <new app repo> --base-url <new app test env URL>
   ```

| Exit | Meaning | Next |
|---|---|---|
| 0 | every scenario passed | record `item_done` at the same commit (it must be reachable from main) |
| 1 | a scenario failed | a `needs_decision` is open: A build it like legacy, or B ask the PO for a business decision |
| 3 | could not run (for example, no row matched) | fix the data or the scenario params; nothing to decide |
| 2 | recorder error, or the worktree is dirty | infrastructure: nothing was judged |

Merge with `--no-ff` (S2's integration already does). After a squash merge, re-run parity at the commit that landed,
because Done needs a pass at the done commit.

A dry run without a browser: `--adapter fixture:<designc>/fixtures/parity-registry/apps/next.json` against a copy of
that fixture registry.
