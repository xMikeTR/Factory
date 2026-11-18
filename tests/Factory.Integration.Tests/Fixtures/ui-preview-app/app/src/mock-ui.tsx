/** Stand-in for @your-org/ui so the preview can be demoed here. At work you use the real library. */
import React from "react";
const t = { space: (n: number) => `${n * 4}px`, ink: "#1d2433", muted: "#5b6478", line: "#e3e6ec", bg: "#f6f7f9", primary: "#2f5bd3",
  tone: { neutral: "#1d2433", success: "#1f7a4d", warning: "#a15c00", danger: "#b42318", info: "#2f5bd3" } as Record<string, string> };
const font = "Inter, -apple-system, Segoe UI, sans-serif";
type Id = { "data-ui-id"?: string };

export const ListDetailLayout = ({ header, summary, filters, main, aside }: Record<string, React.ReactNode>) => (
  <div style={{ fontFamily: font, color: t.ink, background: t.bg, minHeight: "100vh", padding: t.space(6) }}>
    {header}
    <div style={{ display: "flex", gap: t.space(4), margin: `${t.space(4)} 0` }}>{summary}</div>
    {filters}
    <div style={{ display: "grid", gridTemplateColumns: aside ? "1fr 340px" : "1fr", gap: t.space(4), marginTop: t.space(4) }}>
      <div>{main}</div>{aside && <div>{aside}</div>}
    </div>
  </div>
);
export const PageHeader = ({ title, subtitle, actions, ...r }: Id & { title: string; subtitle?: string; actions?: React.ReactNode }) => (
  <div {...r} style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
    <div><h1 style={{ fontSize: 24, margin: 0, fontWeight: 600 }}>{title}</h1>{subtitle && <div style={{ color: t.muted }}>{subtitle}</div>}</div>
    <div style={{ display: "flex", gap: t.space(2) }}>{actions}</div>
  </div>
);
export const Button = ({ label, variant, ...r }: Id & { label: string; variant: string; onClick: () => void }) => (
  <button data-ui-id={r["data-ui-id"]} style={{ font: `500 14px ${font}`, padding: `${t.space(2)} ${t.space(4)}`, borderRadius: 6, cursor: "pointer",
    border: variant === "primary" ? "none" : `1px solid ${t.line}`, background: variant === "primary" ? t.primary : "#fff", color: variant === "primary" ? "#fff" : t.ink }}>{label}</button>
);
const card = { background: "#fff", border: `1px solid ${t.line}`, borderRadius: 8 };
const Skeleton = ({ w = "100%", h = 14 }: { w?: string | number; h?: number }) => <div style={{ width: w, height: h, borderRadius: 4, background: "#e9ecf1", margin: "6px 0" }} />;
const ErrorNote = ({ error }: { error: string }) => (
  <div style={{ color: t.tone.danger, fontSize: 14, padding: t.space(3) }}>Couldn't load this. Try again in a moment.<div style={{ color: t.muted, fontSize: 12 }}>{error}</div></div>
);
export const MetricCard = ({ label, tone = "neutral", value, loading, error, ...r }: Id & { label: string; tone?: string; value: { amount: number; currency: string } | null; loading?: boolean; error?: string }) => (
  <div {...r} style={{ ...card, padding: t.space(4), minWidth: 220 }}>
    <div style={{ color: t.muted, fontSize: 13 }}>{label}</div>
    {loading ? <Skeleton w={160} h={30} /> : error || !value ? <div style={{ fontSize: 28, color: t.muted }}>—</div> :
    <div style={{ fontSize: 28, fontWeight: 600, color: t.tone[tone] }}>{new Intl.NumberFormat("en-US", { style: "currency", currency: value.currency, maximumFractionDigits: 0 }).format(value.amount)}</div>}
  </div>
);
export const FilterBar = ({ fields, ...r }: Id & { fields: string }) => (
  <div {...r} style={{ display: "flex", gap: t.space(2), alignItems: "center" }}>
    {fields.split(",").map((f) => (
      <div key={f} style={{ ...card, padding: `${t.space(2)} ${t.space(3)}`, fontSize: 14, color: t.muted, minWidth: 160 }}>{f[0].toUpperCase() + f.slice(1)} ▾</div>
    ))}
    <span style={{ color: t.primary, fontSize: 14, marginLeft: t.space(2) }}>Clear all</span>
  </div>
);
const statusTone: Record<string, string> = { Validated: "success", Exceptions: "danger", Pending: "warning" };
export const DataTable = ({ rows, density = "comfortable", rowAction: _ra, loading, error, ...r }: Id & { rows: Record<string, unknown>[] | null; density?: string; rowAction?: unknown; loading?: boolean; error?: string }) => {
  const pad = density === "compact" ? t.space(2) : t.space(3);
  if (loading || error || !rows || rows.length === 0)
    return <div {...r} style={{ ...card, padding: t.space(4) }}>
      {loading ? [0, 1, 2, 3, 4].map((i) => <Skeleton key={i} h={18} />) : error ? <ErrorNote error={error} /> :
        <div style={{ textAlign: "center", padding: t.space(8), color: t.muted }}><div style={{ fontWeight: 600, color: t.ink }}>No bordereaux match</div>Try a different cedant or period, or clear the filters.</div>}
    </div>;
  const cols = Object.keys(rows[0] ?? {});
  return (
    <div {...r} style={{ ...card, overflow: "hidden" }}>
      <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
        <thead><tr>{cols.map((c) => <th key={c} style={{ textAlign: "left", padding: pad, color: t.muted, fontWeight: 500, borderBottom: `1px solid ${t.line}`, background: "#fafbfc" }}>{c}</th>)}</tr></thead>
        <tbody>{rows.map((row, i) => (
          <tr key={i} style={{ background: i === 1 ? "#eef3ff" : undefined }}>{cols.map((c) => {
            const v = String(row[c]); const tone = c === "Status" ? statusTone[v] : undefined;
            return <td key={c} style={{ padding: pad, borderBottom: `1px solid ${t.line}` }}>
              {tone ? <span style={{ color: t.tone[tone], background: t.tone[tone] + "14", padding: "2px 8px", borderRadius: 999, fontSize: 12, fontWeight: 500 }}>{v}</span> : v}</td>;
          })}</tr>))}</tbody>
      </table>
    </div>
  );
};
export const DetailPanel = ({ title, record, loading, error, ...r }: Id & { title: string; record: Record<string, string> | null; loading?: boolean; error?: string }) => (
  <div {...r} style={{ ...card, padding: t.space(4) }}>
    <div style={{ fontWeight: 600, marginBottom: t.space(3) }}>{title}</div>
    {loading ? [0, 1, 2, 3].map((i) => <Skeleton key={i} />) : !record ? <div style={{ color: t.muted, fontSize: 14 }}>{error ? "—" : "Select a row to see its details."}</div> :
    Object.entries(record).map(([k, v]) => (
      <div key={k} style={{ display: "flex", justifyContent: "space-between", gap: t.space(3), padding: `${t.space(2)} 0`, borderTop: `1px solid ${t.line}`, fontSize: 14 }}>
        <span style={{ color: t.muted }}>{k}</span><span style={{ textAlign: "right" }}>{v}</span></div>))}
  </div>
);
