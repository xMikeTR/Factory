/**
 * Vitest browser-mode tests generated from a screen spec. Two kinds, one baseline, plus a holdout:
 *
 *   previewTests(spec, ...)  renders the spec with SpecPreview, one test per state. On the first run
 *                            (or with -u) this WRITES the baselines: the screenshots people approve
 *                            and the ARIA tree of each state.
 *   pageTests(spec, Page)    renders the built page with MSW serving the spec's samples for each state,
 *                            and compares against the SAME baselines. Never run with -u: a page that
 *                            differs from the approved preview fails.
 *
 *   holdoutTests(spec, Page) renders the spec and the built page with a SECOND sample the page's author
 *                            never saw, and compares the two ARIA trees. No baseline files, nothing to
 *                            approve. It catches a page written to match the visible sample.
 *
 * Both name their screenshots `ui__<screen>__<state>`; vitest.config.ts maps that to
 * ui-contract/previews/<screen>/<state>-<browser>-<platform>.png, outside the tests'
 * own __screenshots__ folders, so the two tests share one file.
 *
 * Each placement is also captured on its own, as `ui__<screen>__<state>__<placement>`, which maps to
 * previews/<screen>/placements/<state>__<placement>-<browser>-<platform>.png. The page-wide ratio
 * misses a small change (one number turning red); the same ratio on one component does not.
 * People approve the page pictures. The placement files come from the same render.
 */
import React from "react";
import { describe, test, expect, beforeAll, afterAll, afterEach } from "vitest";
import { page, utils } from "vitest/browser";
import { render } from "vitest-browser-react";
import { setupWorker } from "msw/browser";
import { http, HttpResponse } from "msw";
import { SpecPreview, type Registry } from "./SpecPreview";
import { resolveData } from "./fixtures";

type Spec = Parameters<typeof SpecPreview>[0]["spec"] & {
  data: Record<string, { shape: string; source: string; sample?: unknown }>;
  states: Record<string, { pending?: string[]; failed?: string[]; data?: Record<string, unknown>; error?: string }>;
};

const ROOT = "ui-root";
const PIXELS = { comparatorName: "pixelmatch", comparatorOptions: { allowedMismatchedPixelRatio: 0.001 } } as const;
const shotName = (spec: Spec, state: string, placement?: string) =>
  `ui__${spec.screen}__${state}${placement ? `__${placement}` : ""}.png`;
const ariaPath = (spec: Spec, state: string, previewsRel: string) => `${previewsRel}/${spec.screen}/${state}.aria.yml`;
const placementIds = (spec: Spec): string[] =>
  Object.values((spec as any).slots ?? {}).flat().map((p: any) => p.id).filter(Boolean);

const ariaOf = () => utils.aria.renderAriaTree(utils.aria.generateAriaTree(page.getByTestId(ROOT).element()));

/** ARIA tree: what parity's tier 1 and acceptance locators see. */
export async function checkAria(spec: Spec, state: string, previewsRel: string, approvedAria?: Record<string, string>) {
  await expect.element(page.getByTestId(ROOT)).toBeInTheDocument();
  if (!approvedAria) {
    // preview: writes (with -u) or checks the approved tree
    await expect(ariaOf()).toMatchFileSnapshot(ariaPath(spec, state, previewsRel));
  } else {
    // page: data arrives asynchronously, so poll until the tree equals the approved one (or time out)
    const want = approvedAria[`${spec.screen}/${state}`];
    if (want == null) throw new Error(`No approved ARIA tree for ${spec.screen}/${state}. Run the preview tests and get them approved first.`);
    await expect.poll(ariaOf, { timeout: 5000, interval: 100 }).toBe(want.replace(/\n$/, ""));
  }
}

/** Page pixels: what people approve. Retries until two captures agree, then compares. */
export async function checkPixels(spec: Spec, state: string) {
  await expect.element(page.getByTestId(ROOT)).toMatchScreenshot(shotName(spec, state), PIXELS);
}

/** Each placement on its own. A placement with no element is an error, never a skip. */
export async function checkPlacements(spec: Spec, state: string) {
  for (const id of placementIds(spec)) {
    if (!/^[a-z0-9-]+$/.test(id)) throw new Error(`Placement id '${id}' must be lower-case letters, digits and dashes, so it can name a baseline file.`);
    const el = page.getByTestId(ROOT).element().querySelector(`[data-ui-id="${id}"]`);
    if (!el) throw new Error(`Placement '${id}' has no element with data-ui-id="${id}" in state '${state}'. The component must forward data-ui-id to its root node.`);
    await expect.element(page.elementLocator(el)).toMatchScreenshot(shotName(spec, state, id), PIXELS);
  }
}

export async function capture(spec: Spec, state: string, previewsRel: string, approvedAria?: Record<string, string>, placements = true) {
  await checkAria(spec, state, previewsRel, approvedAria);
  await checkPixels(spec, state);
  if (placements) await checkPlacements(spec, state);
}

export function previewTests(spec: Spec, opts: { components: Registry; layouts: Registry; manifests: any[]; previewsRel: string; placements?: boolean }) {
  describe(`${spec.screen} preview`, () => {
    for (const state of Object.keys(spec.states)) {
      test(state, async () => {
        render(
          <div data-testid={ROOT}>
            <SpecPreview spec={spec} state={state} components={opts.components} layouts={opts.layouts} manifests={opts.manifests} />
          </div>,
        );
        await capture(spec, state, opts.previewsRel, undefined, opts.placements ?? true);
      });
    }
  });
}

// Requests held open for `pending` data. Released after each test so the worker can stop.
const held: Array<() => void> = [];
const releaseHeld = () => held.splice(0).forEach((r) => r());

/** MSW handlers for one state: samples, pending (held open until the test ends) and failed (500). */
export function handlersFor(spec: Spec, state: string) {
  const st = spec.states[state] ?? {};
  const values = { ...resolveData(spec.data), ...(st.data ?? {}) };
  return Object.entries(spec.data).map(([key, d]) => {
    const [method, path] = d.source.split(" ");
    const route = (http as any)[method.toLowerCase()];
    return route(path, async () => {
      if (st.pending?.includes(key)) { await new Promise<void>((r) => held.push(r)); return HttpResponse.error(); }
      if (st.failed?.includes(key)) return HttpResponse.json({ error: st.error ?? "failed" }, { status: 500 });
      return HttpResponse.json(values[key] ?? null);
    });
  });
}

/** A recorded response (a minimal HAR, as `factory-s2 replay` writes it): status and a JSON or text body. */
export type ReplayGroup = { method: string; path: string; responses: { status: number; mimeType: string; body: string }[] };

/** Handlers that replay recorded responses: the same method and path are served in recorded order and the last one repeats. */
export function replayHandlers(groups: ReplayGroup[]) {
  return groups.map((g) => {
    let n = 0;
    const route = (http as any)[g.method.toLowerCase()];
    return route(g.path, () => {
      const r = g.responses[Math.min(n++, g.responses.length - 1)];
      return new HttpResponse(r.body, { status: r.status, headers: { "content-type": r.mimeType } });
    });
  });
}

/** approvedAria: the preview's .aria.yml files keyed `<screen>/<state>`, e.g. from
 *  import.meta.glob('../previews/** /*.aria.yml', { query: '?raw', import: 'default', eager: true }) via ariaFiles(). */
export function ariaFiles(glob: Record<string, string>): Record<string, string> {
  return Object.fromEntries(Object.entries(glob).map(([path, text]) => {
    const m = /([a-z0-9-]+)\/([a-z0-9-]+)\.aria\.yml$/.exec(path)!;
    return [`${m[1]}/${m[2]}`, text];
  }));
}

/**
 * One MSW worker for a describe block. MSW 3 renamed `onUnhandledRequest` to `onUnhandledFrame`: the old name is ignored and the
 * default is "warn" (proved in Fixtures/msw/kit-onUnhandledRequest.txt), so this copy of the kit helper uses the new one.
 */
export function useWorker() {
  const worker = setupWorker();
  beforeAll(async () => { await worker.start({ quiet: true, onUnhandledFrame: "error" }); });
  afterEach(() => { releaseHeld(); worker.resetHandlers(); });
  afterAll(() => worker.stop());
  return worker;
}

export function pageTests(spec: Spec, Page: React.ComponentType<any>, opts: { previewsRel: string; approvedAria: Record<string, string>; props?: Record<string, unknown>; placements?: boolean }) {
  describe(`${spec.screen} page`, () => {
    const worker = useWorker();
    for (const state of Object.keys(spec.states)) {
      test(state, async () => {
        worker.use(...handlersFor(spec, state));
        render(<div data-testid={ROOT}><Page {...opts.props} /></div>);
        await capture(spec, state, opts.previewsRel, opts.approvedAria, opts.placements ?? true);
      });
    }
  });
}

/* ---------------------------------------------------------------- holdout */

/** A second sample per data key, kept where the page's author cannot read it. */
export type Holdout = Record<string, unknown>;

const withHoldout = (spec: Spec, holdout: Holdout): Spec => ({
  ...spec,
  data: Object.fromEntries(Object.entries(spec.data).map(([k, d]) => [k, k in holdout ? { ...d, sample: holdout[k] } : d])),
});

/** Why a holdout cannot be used, or null. A holdout that cannot run is never a pass. */
export function holdoutProblem(spec: Spec, holdout: Holdout | undefined): string | null {
  const keys = Object.keys(holdout ?? {});
  if (!keys.length) return `could_not_run: no holdout sample for '${spec.screen}'.`;
  const unknown = keys.filter((k) => !(k in spec.data));
  if (unknown.length) return `could_not_run: holdout names data the spec does not declare: ${unknown.join(", ")}.`;
  if (keys.every((k) => JSON.stringify(holdout![k]) === JSON.stringify(spec.data[k].sample)))
    return `could_not_run: the holdout sample equals the visible sample, so it proves nothing.`;
  return null;
}

type HoldoutOpts = { components: Registry; layouts: Registry; manifests: any[]; holdout: Holdout; props?: Record<string, unknown> };

/** True when the built page, fed the holdout sample, has the same ARIA tree as the spec rendered with it. */
export async function holdoutMatches(spec: Spec, state: string, Page: React.ComponentType<any>, opts: HoldoutOpts, worker: ReturnType<typeof setupWorker>): Promise<boolean> {
  const h = withHoldout(spec, opts.holdout);
  const preview = await render(
    <div data-testid={ROOT}>
      <SpecPreview spec={h} state={state} components={opts.components} layouts={opts.layouts} manifests={opts.manifests} />
    </div>,
  );
  await expect.element(page.getByTestId(ROOT)).toBeInTheDocument();
  const want = ariaOf();
  await preview.unmount();
  worker.use(...handlersFor(h, state));
  await render(<div data-testid={ROOT}><Page {...opts.props} /></div>);
  try { await expect.poll(ariaOf, { timeout: 5000, interval: 100 }).toBe(want); return true; }
  catch { return false; }
}

/**
 * The failure message names the screen and state only. It never prints the trees, because the
 * holdout data is in them and each report would tell the page's author what the holdout holds.
 * Pass `verbose: true` when a person is debugging.
 */
export function holdoutTests(spec: Spec, Page: React.ComponentType<any>, opts: HoldoutOpts & { verbose?: boolean }) {
  describe(`${spec.screen} holdout`, () => {
    const worker = useWorker();
    const problem = holdoutProblem(spec, opts.holdout);
    for (const state of Object.keys(spec.states)) {
      test(state, async () => {
        if (problem) throw new Error(problem);
        if (opts.verbose) {
          const h = withHoldout(spec, opts.holdout);
          const preview = await render(<div data-testid={ROOT}><SpecPreview spec={h} state={state} components={opts.components} layouts={opts.layouts} manifests={opts.manifests} /></div>);
          await expect.element(page.getByTestId(ROOT)).toBeInTheDocument();
          const want = ariaOf();
          await preview.unmount();
          worker.use(...handlersFor(h, state));
          await render(<div data-testid={ROOT}><Page {...opts.props} /></div>);
          await expect.poll(ariaOf, { timeout: 5000, interval: 100 }).toBe(want);
          return;
        }
        if (!(await holdoutMatches(spec, state, Page, opts, worker)))
          throw new Error(`holdout_failed: ${spec.screen}/${state}. The page does not render data it was not shown the way the spec says. Details are withheld on purpose.`);
      });
    }
  });
}
