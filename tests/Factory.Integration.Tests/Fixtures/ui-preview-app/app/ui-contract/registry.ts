// The app's side of the preview contract: the library's components by manifest name, and one layout per template name.
// The Factory never writes this file. A component missing here is still in the catalogue, but it never renders.
import * as UI from "../src/mock-ui";

export const components = {
  Button: UI.Button,
  DataTable: UI.DataTable,
  DetailPanel: UI.DetailPanel,
  FilterBar: UI.FilterBar,
  MetricCard: UI.MetricCard,
  PageHeader: UI.PageHeader,
};

export const layouts = { "list-detail": UI.ListDetailLayout };
