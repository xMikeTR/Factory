import { useState } from "react";
import { PageHeader, Button, MetricCard, FilterBar, DataTable, DetailPanel } from "@your-org/ui";
import { ListDetailLayout } from "@your-org/ui/layouts";
import { useBordereaux, useBordereauxSummary, useBordereau } from "./data/bordereaux";

// Generated from ui-contract/specs/bordereaux.json — change the spec, not this file.
const LOAD_ERROR = "Bordereaux could not be loaded"; // spec states.error.error

export function BordereauxList({ initialSelectedId = "B-2" }: { initialSelectedId?: string }) {
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [selectedId, setSelectedId] = useState<string | undefined>(initialSelectedId);
  const bordereaux = useBordereaux(filters);
  const summary = useBordereauxSummary(filters);
  const selected = useBordereau(selectedId);
  const exportBordereaux = () => {};
  const err = (r: { error: string | null }) => (r.error ? LOAD_ERROR : undefined);

  return (
    <ListDetailLayout
      header={
        <PageHeader data-ui-id="hdr" title="Bordereaux"
          actions={<Button data-ui-id="export" label="Export" variant="secondary" onClick={exportBordereaux} />} />
      }
      summary={<MetricCard data-ui-id="premium" label="Total premium" tone="danger" value={summary.data} loading={summary.loading} error={err(summary)} />}
      filters={<FilterBar data-ui-id="filters" fields="cedant,period" onChange={setFilters} />}
      main={<DataTable data-ui-id="table" density="compact" rows={bordereaux.data} loading={bordereaux.loading} error={err(bordereaux)} rowAction={setSelectedId} />}
      aside={<DetailPanel data-ui-id="detail" title="Bordereau" record={selected.data} loading={selected.loading} error={err(selected)} />}
    />
  );
}
