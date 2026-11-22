# UI contract: rendering and proving screens

Status: **design**. designc's `docs/ui-contract.md` defines the contract: UI packs, screen specs with a state graph, and approving previews. This page is the Factory's half: everything that runs app code. designc decides what a screen should be and records approvals. The Factory renders previews, builds the page, and proves it matches. The UI acceptance gate already works this way: designc writes the criteria, the Factory freezes and runs the specs.

The reference implementation is the standalone kit's `preview/vitest-ui.tsx`, `vitest.config.ts`, `tools/validate.ts` and `tools/check-code.ts`. It is tested against a demo: 5 states rendered, the built page passing all 5, and a two-style drift failing the 2 states where it shows.

## Tooling

Vitest browser mode (Playwright provider, Chromium) runs both kinds of test in a real browser with the app's real library:

- **Preview tests** render the spec with the generic `SpecPreview`, one test per state, and write per state a screenshot (`<state>-<browser>-<platform>.png`) and an ARIA tree (`<state>.aria.yml`).
- **Page tests** render the built page, with MSW serving the spec's samples per state (`pending` held open, `failed` answers 500). The kit helper sets `onUnhandledRequest: 'error'`, which MSW 3 ignores (it is `onUnhandledFrame` now, default `warn`), so an unlisted call is not stopped there: the request log (§7) is what catches it. They check the same files: they poll until the ARIA tree equals the approved one, then `toMatchScreenshot` (pixelmatch, retried until two captures agree).

Both name screenshots `ui__<screen>__<state>`. `resolveScreenshotPath` maps that to `ui-contract/previews/<screen>/`, so one approved file serves both.

**Placement screenshots.** Each placement is also captured on its own, named `ui__<screen>__<state>__<placement>`, and lands in `ui-contract/previews/<screen>/placements/<state>__<placement>-<browser>-<platform>.png`. Placement ids match `[a-z0-9-]+`. They are hashed in `ui_preview_rendered` and `ui_preview_approved` (the copy in step 3 is recursive). People approve page pictures only; placement pictures follow from them.

Pin the Vitest version. `toMatchScreenshot` is marked experimental in the API docs, ARIA snapshots are experimental (since 4.1.4), and trace view is 5.0+. The kit runs on 5.1.0.

## 1. Preview run

Status: **built (F1)**. The app provides `ui-contract/preview/` (the kit's helper), `ui-contract/registry.ts` (`components` by manifest name, `layouts` by template name) and a `ui-preview` Vitest project; the Factory writes the spec, manifests and generated test into a scratch worktree of HEAD, never into your checkout. Only the runner image renders (`LocalSandbox` exits 2). Vitest 5 writes its JSON report to `.vitest/json/output.json`; the run reads it from there.

`factory-s2 preview --registry <dir> --screen <screen> --repo <app>`. This runs before any freeze, on a minted or bound spec. No agent: the preview test is generated from the spec (one `previewTests(spec, …)` call).

1. Refuses a dirty worktree, a spec that fails designc's spec checks (via the headless core), or a screen with no spec.
2. In the sandbox (the runner image, network none apart from the gated restore): writes the generated test, runs `FACTORY_UI_PREVIEW_CMD` (default `npx vitest run --project ui-preview -u --reporter=json`).
3. Copies `ui-contract/previews/<screen>/` into the registry's `ui/previews/<screen>/` and writes one `ui_preview_rendered {screen, spec_hash, files: {name: sha256}, image}` row. `image` is the runner image digest: baselines are platform- and font-specific, so the image is part of what was approved.
4. Exits 0 rendered, 3 `could_not_run` (a component missing from the app's registry, a state that never renders), 2 error. Like `parity`: `could_not_run` is never a pass.

designc's **Approve previews** then records `ui_preview_approved` with the same hashes. Approval is human only and designc's.

## 2. In a run

Status: built (F2), except the holdout (step 4, F4). Where the build differs from the first sketch: the Factory seeds the contract once per run on the integration branch (not into each worktree); the generated page test has three separate tests per state (`aria`, `pixels`, `placements`) so each kind is independent; the generated test renders `<Page />` with no props, so a page must reach every spec state by default (a page that needs props is a spec gap); `ui_check_failed` stays in the run ledger, and only `ui_result` and the run outcome are projected (the `caught_by` stages move to F8); a run that cannot run the page tests (no runner image, no report) is `could_not_run`, exit 3, and ends `integration_unrunnable`, never a pass.

The handover's `ui.json` (`designc.handover.ui/1`) lists, per screen: the bound spec, the pack manifests and templates it uses, and the approved preview files with hashes. `Handover.Ui` is an interface widening, like `WorkItem` was.

**Setup.** At worktree creation the Factory writes the frozen previews into `ui-contract/previews/<screen>/` and the frozen spec into `ui-contract/specs/`. It also writes the generated page test, always (the spec carries `component` plus an optional `file` field naming the page file). The test author does not write it.

**Test first.** The page test is the test author's red evidence: on the baseline, the page does not exist or does not match, so it fails. The implementer turns it green. Deterministic verify applies unchanged: red, green, the stashed-implementation run, pass-to-pass.

**Integration verify**, for `done_when: ui: <screen>`, in this order:

1. **Drift.** Hash check of `ui-contract/previews/**` (placements included), `ui-contract/specs/**`, the generated tests, `vitest.config.*` and `preview/vitest-ui.*` against the handover. Any change is a hard fail, `ui_baselines_modified`, and no check runs. Same rule as `acceptance_specs_modified`. The config and helper are frozen too, because loosening a threshold is a way to fake done.
2. **Code check.** The spec against the page source: every placement id present on the right component, nothing unspecified, imports only from the library, no inline styles or raw px or hex values. Port `check-code.ts` to `Core/UiSpec.fs` (pure, F#) so the run decides without Node. Failures: `ui_check_failed {screen, kind: code, detail}`.
3. **Page tests.** `FACTORY_UI_CMD` (default `npx vitest run --project ui-page --reporter=default --reporter=json --reporter=html`: the default reporter prints each failure's diff and step timeline, json is the report the check reads, html is kept as evidence, only when the check fails). Each failing state is `ui_check_failed {screen, state, kind: aria | pixels}`; a failing placement is `{screen, state, placement, kind: placement-pixels}`, and a placement element missing at run time is `kind: placement-missing` (it overlaps `UiGuard` `ContractMissing` on purpose: one static, one at run time). Failures are attributed to the tasks whose element owns the screen (as `PlaywrightReport` does for acceptance). The run ends `integration_failed`.
4. **Holdout** (built, F4). Runs after the page tests, only when everything visible passed, and before QA: nothing deterministic runs after it. The holdout sample lives in the registry (`ui/holdout/<screen>.json`, one value per data key, written by a person) and is never written into a worktree or read by any designc stage; the handover lists it by hash only. The Factory process checks the file against that hash (a change since the freeze, or a file gone, is `could_not_run`), writes the sample and a generated test into a scratch detached checkout of the integration branch, runs `FACTORY_UI_HOLDOUT_CMD` (default `npx vitest run --project ui-holdout --reporter=json`) in the runner image and compares ARIA trees exactly; the scratch copy is deleted afterwards. The app's Vitest config must define a `ui-holdout` project, or the result is `could_not_run`. A screen with no holdout is a `verify` row `no_holdout` (coverage), not a failure. A failure is `holdout_failed {screen, state}` for a UI holdout, or `holdout_failed {screen, criterion, state}` for an acceptance case marked `holdout: true`: never trees, steps or data, and no runner output reaches a ledger row, a brief or the run's detail. Each row is attributed to the tasks whose element owns the screen (or the criterion's element). Exit 3 is `could_not_run`, never a pass, and `ui_result` is `could_not_run`. The agent's feedback is the screen (and criterion) and state only. Acceptance holdout cases: the handover lists them by hash (`ui.json` `holdout_cases`). The Factory process runs designc's headless `holdout-spec --registry <dir> --criterion <id> --case <n> --json` (`FACTORY_DESIGNC_DIR`), compares the printed `sha256` with the handover's, writes the spec to `tests/acceptance/<element-slug>/<criterion>.holdout-<case>.spec.ts` in the scratch copy and runs it with `FACTORY_ACCEPTANCE_CMD` narrowed to that file. Without `FACTORY_DESIGNC_DIR`, or on a hash mismatch, the result is `could_not_run`. Retry cap: `FACTORY_HOLDOUT_RETRIES` (default 2) is counted per (screen) or (screen, criterion) across the runs of one work item (or of one handover when it has no work item), this run included. The third failing run raises a `needs_decision` ("holdout <screen>/<state> failed N times"; A: keep the holdout and a person writes guidance for the build; B: the holdout is wrong, change it in designc and freeze again) and ends `integration_failed`; no further repair is attempted.

Rows project into the registry ledger as designc's escape stages `ui-code`, `ui-visual` and `holdout` (`caught_by`). `ui-visual` includes the placement kinds.

## 3. Guards

Status: built (F3). Both policy layers, `ScopedWorkspace` (snapshot refusal, holdout read and list, the UI paths in `S2Config.Default.ProtectedPaths`) and `UiGuard` placement ids are in; the sabotage rows below run in `Sabotage/UiSabotageTests.cs` and `SabotageTests.cs`.

Four kit-hook rows flip to DENY here (the hook allows them on a draft screen): writing a draft spec, writing a new spec, rendering a draft screen's previews, and rendering through the npm script.

**Policy.** The row list is the kit hook's test table (`.claude/hooks/ui-contract-guard.test.mjs`). For `RoleAllowListPolicy` and `policies/s2/*.yaml`, every Factory build role is denied:
- writes to `ui-contract/previews/**` (placements included) and `ui-contract/specs/**`, always. Unlike the hook, there is no draft-screen exception: the Factory never hand-writes previews. The same applies to `ui-contract/preview/**` (the helper), the hook itself, and the generated tests;
- any read, search, shell read or write of `ui-contract/holdout/**` and `ui/holdout/**`;
- a sandbox command containing `vitest` with `-u`, `--update` or `update`, on any project, with or without a screen named, including after a newline or `&&`; `npm run ui:preview` is a preview render and is denied for build roles too;
- shell writes into those paths (`rm`, `cp`, `sed -i`, redirects, PowerShell `Copy-Item`, `git checkout <ref> -- ui-contract/previews`);
- reaching any of them through `..`, an absolute path, a subfolder-relative path or Windows separators. Write scope is checked on the resolved path.

Still allowed: reading approved previews and specs, writing the page and page tests, `npx vitest run --project ui-page`, `npx vitest related`, and unrelated commands with a `-u` flag (`git push -u`, `sort -u`).

The preview run is not an agent run, so it is outside these rules. Contract-tested against both policy layers, like the read-only references.

**UiGuard.** A new `UiItemKind.PlacementId`, scanned from `data-ui-id` attributes. The spec's placement ids are contract items only for the task that owns the page (it writes the spec's `file`, or links the element named like the component), so a missing `data-ui-id` is `ContractMissing` at that task's verify. The guard runs whenever the handover binds screens, with or without a `ui:` policy section. Any task dropping a `data-ui-id` the baseline had is `Removed`. A page test that is gone or skipped is already caught as `UiTest` or `SkippedTest`.

**AssertionGuard.** No change: it already counts Vitest `expect` calls. The generated tests loop over states, so the count only drops if a state is removed from the spec, and the spec is frozen.

**Sabotage tests**, each against the real component:

| Attack | Caught by |
| --- | --- |
| Implementer runs `vitest -u` | Policy refusal |
| Edits a preview PNG or `.aria.yml` | Drift, `ui_baselines_modified` |
| Raises `allowedMismatchedPixelRatio` in the config or helper | Drift (config and helper frozen) |
| Skips or deletes a state's test | `UiGuard` `SkippedTest`/`Removed` |
| Drops a placement's `data-ui-id` | `UiGuard` `ContractMissing`, code check |
| Styles inline to match the picture | Code check (inline style, raw values) |
| Wrong token on one placement | Page tests, `placement-pixels` |
| Page hard-coded to the visible sample | Holdout, `holdout_failed` |
| Writes a preview through `..` (`src/pages/../../ui-contract/previews/…`, `./src/../ui-contract/previews/…`, `../ui-contract/previews/…`) | Write scope on the resolved path (`ScopedWorkspace`) |
| `run_command` of `"npm test\nnpx vitest -u"` (also `\r\n`) | Command parser refuses the whole command; nothing reaches the sandbox |
| Update command on a second line | Command parser, policy |

## 4. Feedback to agents

**Built (F7).** On failure, the agent gets text before pictures, which keeps tokens per passing task down. The ARIA diff and the step timeline come from the terminal report (the JSON failure messages are truncated and carry no trace); `UiFeedback` (Core) parses them, and the text rides on `ui_check_failed` as `feedback`, with `images` (diff pictures copied to `evidence/ui/<screen>/<state>/`). `Repair` turns these rows into the repair brief; `repair.json` has `Images` per task, and the brief message carries them as PNGs. The HTML report is copied to `evidence/ui/<screen>/html/` with a `verify` row `ui` / `evidence`. Recorded real output: `tests/Factory.Integration.Tests/Fixtures/ui-feedback/`.

1. **The ARIA diff first.** The expected and received lines name the missing or changed control directly ("Export button missing", "error text differs"). Most failures are fixed from this alone.
2. **The Vitest trace's step timeline,** as text: which steps ran, how long, which locator. Enable `browser.traceView: 'failure-only'` and the HTML reporter, and attach the report as run evidence for the QA gate and the `s2-diagnosis` investigator.
3. **The diff image last,** only when the ARIA tree matches but the pixels don't (a style problem).

## 5. UxReview

**Built (F7).** For a shot of a screen's component element, the judge gets the intent, then `Approved design` and `ui-contract/previews/<screen>/<state>-chromium-linux.png` (checked against the handover's sha256; a missing or changed file leaves it out with a note), then `Built page` and the shot. The breakpoint is not matched. The rubric gained a line about departure from the approved design, so its hash changed.

`UxReview` already reads freeze-time screenshots named `<element-slug>__<state>__<breakpoint>.png`. Feed it the approved preview and the built page's screenshot per state. The judge explains differences against the picture the person approved, not just the acceptance text. It stays a gradient: never a gate, never blocks.

## 6. UI mutation (built, F6)

A visual test with too loose a threshold lets real changes through. In the kit's demo, changing the metric's tone failed `ready` and `filtered` but passed `empty`: the red "$0" was under the page-wide 0.1% ratio. That survivor is now killed by default, because placements are screenshotted on their own (`placement-pixels`).

What remains is generated mutants, built in F6: `UiMutants.generate` makes them from the spec, manifests and frozen tokens (swap each token prop to another value of its group, remove each placement), and `UiMutantCheck` runs them in a scratch checkout after the holdout, in report mode. Every mutant must fail at least one page test; survivors are reported in a `ui_mutants` row, never blocking. Cap: `FACTORY_UI_MUTANTS_MAX`. Code mutation per task is whole-file with the changed-line filter; the full run is `factory-s2 mutation-full` after merge, not nightly.

## 7. For parity

What Vitest adds to the Factory's `parity` command and the legacy migration. Vitest can only render the new app (it cannot render Blazor components), so it complements the recorder rather than replacing it.

- **Same data on both sides (built, F9).** `factory-s2 replay --registry <dir> --scenario <id> [--repo .]` renders the scenario's screen once in the runner image against the HAR designc recorded (`parity/har/<id>-legacy.har`, scenario `screen:` picks the page and spec). The HAR becomes MSW handlers through the helper's `replayHandlers`: only JSON and text responses are kept, numeric and uuid path segments become `:id` (so a page asking for another record than the recorded one is still served), the same method and path are served in recorded order and the last one repeats. It works from a scratch copy of the repo's HEAD (the checkout is never written), writes the entries and a generated `ui-contract/tests/<screen>.replay.page.test.tsx` there (`.page.test.tsx` so the kit's `ui-page` project runs it) and runs `FACTORY_UI_CMD` narrowed to that file. It writes no ledger row and prints endpoints only. Exit 0 every request served from the HAR, 1 the page made a request the HAR lacks, 3 could not run, 2 error (no scenario, HAR or spec, dirty repo). `@mswjs/source` is not used: a minimal HAR (method, url, status, mime type, text) is enough.
- **Request parity (built, F9).** The generated page test logs every request MSW saw per state, matched or not, to `.factory/requests/<screen>/<state>.json` (the app's Vitest config needs `api: { allowWrite: true }`; without a log the screen is `could_not_run`). The `ui:` check reads them with `RequestParity` (pure, in Core) and fails with `ui_check_failed {kind: "request", detail}` for: an endpoint not in the spec's `data.source` (`unlisted request`); a `data.source` endpoint no state called (`never called`); a request body whose shape differs from the legacy request of the same endpoint in the registry's baseline `requests` (`request shape`; compared only when a baseline of a scenario with this `screen:` recorded requests). A legacy endpoint the spec does not list is only a note. `:param` in a spec source matches any one segment. Shapes use a port of designc's `Parity.shape`, tested against designc's `parity-cases.json`. Detail holds endpoints and shapes, never values. In the F8 stage verdicts `request` counts as `ui-code`: a wrong or missing endpoint is a code fault, not a look.
- **Pixel noise.** Tier 2's fixed per-channel threshold can be swapped for a perceptual comparator (SSIM or GMSD via Vitest's `comparators`) where fonts make anti-aliasing noisy. Only if tier 2 proves flaky.
- **Debugging a parity failure.** Trace view snapshots the DOM and, since 5.1, the ARIA tree per step. Run the scenario's steps as a page test and replay the failing step.
- **What not to change.** Tier 1's normaliser (shape not data, one record per table, masks, `expect` sort and filter) does more than Vitest's ARIA matcher. Keep it. Vitest's `contain` matching and regex names are useful for hand-written checks, not as a replacement.

Known gap: `npm test -- -u` (Vitest reached through an npm script alias) is not seen by the `vitest` token rule; the sandbox command allow-list is the mitigation, and drift catches any changed picture at integration verify.

Not useful here: in-source testing, benchmarking, type testing. Sharding (`--shard` with the blob reporter and `--merge-reports`) is worth having once one run builds many screens.

## 8. System 3 rows (F8)

**Built (F8).** The registry projection gains designc's `stage_verdict` (pipeline stream): what each verification stage said, so System 3 can compute time to signal, escape rate, and veto and recovery. The Factory writes the rows; designc computes the numbers (D7 part 2).

| Source in the run ledger | Verdict |
|---|---|
| `ui_check_failed` kind `code` | `ui-code` `fail`, one row per (task, screen), `failures: [{kind}]` |
| `ui_check_failed` kind `aria`, `pixels`, `placement-pixels`, `placement-missing` | `ui-visual` `fail`, same grouping, `failures: [{state, kind, placement?}]` |
| `ui:<screen>` verify that passed, no `ui_check_failed` for that screen in the run | `ui-code` `pass` and `ui-visual` `pass`, no task (exit 3: `could_not_run`) |
| `holdout_failed` | `holdout` `fail`, per (task, screen): `{state}`, or `{criterion, state}` for an acceptance case |
| holdout verify row `pass` / `could_not_run` / `no_holdout` | `pass` / `could_not_run` / no row. A `fail` that blamed no task is a run-level `fail` with its states |
| `ui_acceptance_failed` | `acceptance` `fail` per task, `failures: [{criterion}]` |
| acceptance verify row (stage `integration_verify`) | `pass`; a `failed` one that blamed no task is a run-level `fail`. The holdout's own acceptance run is ledgered under stage `holdout_acceptance` and gives no acceptance verdict |
| `review` row | one per round with `round`: `passed`, `accepted` -> `pass`; `blocked` -> `fail`; `malformed` -> `could_not_run` |
| `verify` stage `qa` | run level: `passed` -> `pass`, `failed` -> `fail`, `timeout` -> `could_not_run` |

- `change_ts` is the epoch ms of the task's latest `verify_requested` task event not after the verdict (a run-level verdict: the latest of any task); `null` when there is none, so time to signal skips it. The ledger rows already carry `ts`, so nothing new is timed.
- A verdict carries state names, kinds, placement ids and criterion ids only. No `detail`, tail, feedback, trace path or finding text, so nothing a holdout checks can reach designc.
- `drift` has no run-ledger row today (it ends the run as `integration_failed`), so it is not a verdict.
- Fixture: `tests/Factory.Integration.Tests/Fixtures/system3/run.jsonl` (hand-written run) and `projected.jsonl` (its projection, validated against the pinned schema; regenerate with `FACTORY_UPDATE_GOLDEN=1`). designc's D7 part 2 uses it.
- `BugIntake` accepts the 13 `caught_by` values and the `catalogue_gap` origin; a test compares its list with the pinned schema.

## 9. Scenario tests (F1)

desigc's `scenarios.json` (pinned: `handover-scenarios.example.json`) lists generated tests `ui-contract/tests/<screen>.scenarios.page.test.tsx`. `DesigncHandoverReader` verifies each sha256 and refuses the handover on a mismatch; `UiContractSeed` writes them verbatim into the seed commit, so they are in the drift baseline and read-only to agents. The `ui:` run executes them with the page tests; `UiReport.scenarioFailures` reads describe `"<screen> scenarios"`, titles `"<id> example <n>"`, and maps each failure to `body` (wrong value), `not_sent` (nothing sent) or `other`. Page-test counting ignores them. The verdict is a `stage_verdict` row, stage `scenario`, with `failures[].criterion` = `<id> example <n>` and `scenario` set when exactly one scenario failed; escapes tied to a scenario are `spec_gap`. Proof: `ScenarioDockerTests` (page sending the right body passes, wrong field name fails naming scenario and example), `UiContractSeedTests` (edit is drift).

## Slices

1. **Preview run.** `factory-s2 preview`, `ui_preview_rendered`, the generated preview test, sandbox command and exit codes.
2. **`ui:` check.** `Handover.Ui`, setup, drift, `Core/UiSpec.fs` code check, `FACTORY_UI_CMD`, `ui_check_failed`, projection.
3. **Guards (built, F3).** Policy rows, `UiItemKind.PlacementId`, the sabotage table.
4. **Feedback and evidence.** ARIA diff first, trace view text, HTML report as evidence; `UxReview` against approved previews.
5. **Parity.** HAR to MSW handlers, request logging and comparison.
6. **UI mutation**, report mode, incremental per task and full at merge.
7. **Holdout.** `holdout_failed`, the scratch copy, the retry cap.
8. **System 3 rows (F8).** `stage_verdict` projection.

## Open questions

- Where the preview run executes for approval: the same runner image as integration verify, so the platform and font parts of the baseline match. This page assumes yes.
- Decided: the page test is always generated, with an optional `file` field on the spec schema.
- Breakpoints: decided, one viewport and no `viewports` field. Multiple breakpoints would be listed as coverage.
