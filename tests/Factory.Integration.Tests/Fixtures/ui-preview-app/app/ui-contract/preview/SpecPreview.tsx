/**
 * Renders a screen spec with the REAL library components and sample data, so the
 * team approves what the screen will look like before any page code is written.
 *
 * Library-agnostic: you pass in a registry of components and one layout per template.
 * Rendered per state by preview/vitest-ui.tsx (Vitest browser mode). The approved screenshots
 * and ARIA trees become the baselines the built page is checked against.
 *
 * Conventions (same as generated code, so preview and page render identically):
 *   - top-level slot "main"        → layout prop `main`
 *   - subslot "header.actions"      → prop `actions` on the component(s) placed in "header"
 *   - every placement gets data-ui-id = placement.id
 *   - action props get a no-op handler; data props get the spec's sample (or a fixture)
 *   - `state` picks one state of the spec's state graph: its `data` overrides the samples
 *     (null = no value), keys in `pending` give bound components `loading: true`, keys in `failed`
 *     give them `error`. Render every state to approve the whole screen, not just ready.
 */
import React, { type ComponentType, type ReactNode } from "react";
import { resolveData } from "./fixtures";

export type Registry = Record<string, ComponentType<any>>;
type Placement = { id?: string; component: string; props?: Record<string, unknown>; bind?: Record<string, string> };
type Spec = {
  screen: string; template: string; data: Record<string, any>; slots: Record<string, Placement[]>;
  states?: Record<string, { initial?: boolean; data?: Record<string, unknown>; pending?: string[]; failed?: string[]; error?: string }>;
};
type Manifest = { name: string; props?: Record<string, { type: string }> };

export function SpecPreview(props: {
  spec: Spec;
  components: Registry;
  layouts: Registry;            // keyed by template name
  manifests: Manifest[];
  state?: string;               // default: the 'ready' state
}) {
  const { spec, components, layouts, manifests } = props;
  const stateName = props.state ?? "ready";
  const st = spec.states?.[stateName];
  if (props.state && !st) return <Missing name={`state '${props.state}'`} />;
  const data: Record<string, unknown> = { ...resolveData(spec.data), ...(st?.data ?? {}) };
  const manifestByName = new Map(manifests.map((m) => [m.name, m]));
  const slotPaths = Object.keys(spec.slots ?? {});

  const renderSlot = (path: string): ReactNode[] =>
    (spec.slots[path] ?? []).map((pl, i) => {
      const C = components[pl.component];
      if (!C) return <Missing key={i} name={pl.component} />;
      const m = manifestByName.get(pl.component);
      const p: Record<string, unknown> = { ...pl.props };
      // action props: the spec names a handler; in preview it does nothing
      for (const [k, def] of Object.entries(m?.props ?? {})) if (def.type === "action" && k in p) p[k] = noop;
      for (const [dataProp, key] of Object.entries(pl.bind ?? {})) {
        p[dataProp] = data[key];
        if (st?.pending?.includes(key)) { p[dataProp] = null; p.loading = true; }
        if (st?.failed?.includes(key)) { p[dataProp] = null; p.error = st.error ?? "Request failed"; }
      }
      // direct subslots of this slot become props on the placed component
      for (const child of slotPaths.filter((s) => s.startsWith(path + ".") && !s.slice(path.length + 1).includes(".")))
        p[child.slice(path.length + 1)] = <>{renderSlot(child)}</>;
      return <C key={pl.id ?? i} data-ui-id={pl.id} {...p} />;
    });

  const Layout = layouts[spec.template];
  if (!Layout) return <Missing name={`layout for template '${spec.template}'`} />;
  const slotProps = Object.fromEntries(
    slotPaths.filter((s) => !s.includes(".")).map((s) => [s, <>{renderSlot(s)}</>]),
  );
  return <div data-ui-state={stateName}><Layout {...slotProps} /></div>;
}

const noop = () => {};
const Missing = ({ name }: { name: string }) => (
  <div style={{ border: "2px dashed #c00", padding: 8, color: "#c00", fontFamily: "monospace" }}>
    not in registry: {name}
  </div>
);
