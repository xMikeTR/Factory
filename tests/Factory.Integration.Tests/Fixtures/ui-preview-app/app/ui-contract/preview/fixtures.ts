/**
 * Sample data for previews. Prefer `sample` on each data entry in the spec: realistic
 * values the reviewer recognises (real cedant names, plausible premiums). The agent writes
 * these from the ticket and existing fixtures. These generic fallbacks only stop the
 * preview breaking when a sample is missing, and they look generic on purpose so a
 * reviewer notices.
 */
export function resolveData(data: Record<string, { shape: string; sample?: unknown }>) {
  return Object.fromEntries(Object.entries(data ?? {}).map(([k, d]) => [k, d.sample ?? fallback(d.shape, k)]));
}

function fallback(shape: string, key: string): unknown {
  switch (shape) {
    case "number": return 1234;
    case "money": return { amount: 1250000, currency: "USD" };
    case "percent": return 0.42;
    case "text": return `Sample ${key}`;
    case "date": return "2026-01-01";
    case "boolean": return true;
    case "record": return { Name: `Sample ${key}`, Status: "Active", Updated: "2026-01-01" };
    case "list": return ["Item one", "Item two", "Item three"];
    case "series": return Array.from({ length: 12 }, (_, i) => ({ x: `M${i + 1}`, y: 50 + ((i * 37) % 40) }));
    case "table":
      return Array.from({ length: 5 }, (_, i) => ({ Id: `${key}-${i + 1}`, Name: `Sample row ${i + 1}`, Status: "Active" }));
    default: return null;
  }
}
