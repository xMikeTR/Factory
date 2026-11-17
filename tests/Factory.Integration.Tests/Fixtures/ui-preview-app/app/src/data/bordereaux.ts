// Demo data hooks: plain fetch, so MSW can serve the spec's samples in tests.
import { useEffect, useState } from "react";

export type Result<T> = { data: T | null; loading: boolean; error: string | null };
function useApi<T>(url: string | null): Result<T> {
  const [r, setR] = useState<Result<T>>({ data: null, loading: !!url, error: null });
  useEffect(() => {
    if (!url) return;
    let live = true;
    setR({ data: null, loading: true, error: null });
    fetch(url)
      .then(async (res) => { if (!res.ok) throw new Error(String(res.status)); return res.json(); })
      .then((data) => live && setR({ data, loading: false, error: null }))
      .catch((e) => live && setR({ data: null, loading: false, error: String(e) }));
    return () => { live = false; };
  }, [url]);
  return r;
}
export const useBordereaux = (_filters: unknown) => useApi<Record<string, unknown>[]>("/api/bordereaux");
export const useBordereauxSummary = (_filters: unknown) => useApi<{ amount: number; currency: string }>("/api/bordereaux/summary");
export const useBordereau = (id: string | undefined) => useApi<Record<string, string>>(id ? `/api/bordereaux/${id}` : null);
