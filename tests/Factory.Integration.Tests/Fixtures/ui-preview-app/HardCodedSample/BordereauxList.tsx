import { useState } from "react";
import { PageHeader, Button, MetricCard, FilterBar, DataTable, DetailPanel } from "@your-org/ui";
import { ListDetailLayout } from "@your-org/ui/layouts";
import { useBordereaux, useBordereauxSummary, useBordereau } from "./data/bordereaux";
import spec from "../ui-contract/specs/bordereaux.json";

// SABOTAGE (kit examples/sabotage/pages.tsx, HardCodedSample): whenever the API answers, show the spec's visible sample instead.
const LOAD_ERROR = "Bordereaux could not be loaded"; // spec states.error.error

export function BordereauxList({ initialSelectedId = "B-2" }: { initialSelectedId?: string }) {
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [selectedId, setSelectedId] = useState<string | undefined>(initialSelectedId);
  const bordereaux = useBordereaux(filters);
  const summary = useBordereauxSummary(filters);
  const selected = useBordereau(selectedId);
  const exportBordereaux = () => {};
  const rows = bordereaux.data?.length ? (spec.data.bordereaux.sample as Record<string, unknown>[]) : bordereaux.data;
  const total = summary.data?.amount ? spec.data.totalPremium.sample : summary.data;
  const record = selected.data ? (spec.data.selected.sample as Record<string, string>) : selected.data;
  const err = (r: { error: string | null }) => (r.error ? LOAD_ERROR : undefined);

  return (
    <ListDetailLayout
      header={
        <PageHeader data-ui-id="hdr" title="Bordereaux"
          actions={<Button data-ui-id="export" label="Export" variant="secondary" onClick={exportBordereaux} />} />
      }
      summary={<MetricCard data-ui-id="premium" label="Total premium" tone="neutral" value={total} loading={summary.loading} error={err(summary)} />}
      filters={<FilterBar data-ui-id="filters" fields="cedant,period" onChange={setFilters} />}
      main={<DataTable data-ui-id="table" density="compact" rows={rows} loading={bordereaux.loading} error={err(bordereaux)} rowAction={setSelectedId} />}
      aside={<DetailPanel data-ui-id="detail" title="Bordereau" record={record} loading={selected.loading} error={err(selected)} />}
    />
  );
}
