module Factory.Core.Tests.UiMutantsTests

open Factory.Core
open Factory.Core.Tests.Support

let private spec =
    """{"screen":"b","slots":{
        "header.actions":[{"id":"export","component":"Button","props":{"label":"Export","variant":"secondary","onClick":"go"}}],
        "summary":[{"id":"premium","component":"MetricCard","props":{"label":"Total premium","tone":"neutral"}}],
        "main":[{"id":"table","component":"DataTable","props":{"density":"compact","rowAction":"pick"}}]}}"""

let private manifests =
    [ """{"name":"MetricCard","props":{"label":{"type":"string"},"tone":{"type":"token","token":"tone"}}}"""
      """{"name":"DataTable","props":{"density":{"type":"token","token":"density"},"rowAction":{"type":"action"}}}"""
      """{"name":"Button","props":{"label":{"type":"string"}}}""" ]

let private tokens =
    """{"tone":{"neutral":{"$value":"n"},"success":{"$value":"s"},"danger":{"$value":"d"},"$description":"x"},
        "density":{"compact":{"$value":"1"},"comfortable":{"$value":"2"}}}"""

let private page =
    """<ListDetail
  header={<PageHeader data-ui-id="hdr" actions={<Button data-ui-id="export" label="Export" onClick={() => go()} />} />}
  summary={<MetricCard data-ui-id="premium" label="Total premium" tone="neutral" value={s.data} />}
  main={<DataTable data-ui-id="table" density={density} rows={r}>
          <DataTable data-ui-id="inner" />
        </DataTable>}
/>"""

let private find id =
    UiMutants.generate spec manifests (Some tokens) |> fst |> List.find (fun m -> m.Id = id)

let tests : (string * (unit -> unit)) list =
    [ "ui mutants: token props get one mutant per other group value, every placement gets a remove", fun () ->
          let ms, notes = UiMutants.generate spec manifests (Some tokens)
          equal [] notes "no notes"
          equal
              [ "export:remove"
                "premium:tone:neutral->success"; "premium:tone:neutral->danger"; "premium:remove"
                "table:density:compact->comfortable"; "table:remove" ]
              (ms |> List.map (fun m -> m.Id)) "ids in spec order"

      "ui mutants: without frozen tokens there are no token mutants and a note says so", fun () ->
          let ms, notes = UiMutants.generate spec manifests None
          equal [ "export:remove"; "premium:remove"; "table:remove" ] (ms |> List.map (fun m -> m.Id)) "removes only"
          equal 1 notes.Length "one note"

      "ui mutants: a token mutant changes only that prop, as the kit's WrongTone does", fun () ->
          match UiMutants.apply page (find "premium:tone:neutral->danger") with
          | Ok code -> equal (page.Replace("tone=\"neutral\"", "tone=\"danger\"")) code "only tone changed"
          | Error e -> failwith e

      "ui mutants: a prop that is not a literal is skipped", fun () ->
          match UiMutants.apply page (find "table:density:compact->comfortable") with
          | Error e -> equal "skipped: not a literal" e "skipped"
          | Ok _ -> failwith "should be skipped"

      "ui mutants: remove deletes a self-closing element, leaving null where it was a whole expression", fun () ->
          match UiMutants.apply page (find "premium:remove") with
          | Ok code ->
              equal true (code.Contains "summary={null}") "null"
              equal false (code.Contains "MetricCard") "gone"
          | Error e -> failwith e

      "ui mutants: remove keeps an element's arrow-function props intact and finds paired elements by depth", fun () ->
          match UiMutants.apply page (find "export:remove") with
          | Ok code -> equal true (code.Contains "actions={null}") "button removed"
          | Error e -> failwith e
          match UiMutants.apply page (find "table:remove") with
          | Ok code ->
              equal true (code.Contains "main={null}") "table with its nested table removed"
              equal false (code.Contains "DataTable") "no table left"
          | Error e -> failwith e

      "ui mutants: a placement the page lacks is skipped", fun () ->
          match UiMutants.apply "<div/>" (find "premium:remove") with
          | Error e -> equal true (e.StartsWith "skipped") "skipped"
          | Ok _ -> failwith "should be skipped" ]
