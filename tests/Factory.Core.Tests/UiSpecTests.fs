module Factory.Core.Tests.UiSpecTests

open Factory.Core
open Factory.Core.Tests.Support

let private spec =
    """{"screen":"bordereaux","component":"BordereauxList","states":{"default":{},"empty":{},"error":{}},
        "slots":{"header":[{"id":"hdr","component":"PageHeader"}],"main":[{"id":"table","component":"DataTable"}]}}"""

let private manifests =
    [ """{"name":"PageHeader","import":"@your-org/ui"}"""
      """{"name":"DataTable","import":"@your-org/ui"}"""
      """{"name":"Banner","import":"@your-org/ui"}""" ]

let private good =
    """import { PageHeader, DataTable } from "@your-org/ui";
export function BordereauxList() {
  return <div><PageHeader data-ui-id="hdr" title="Bordereaux" /><DataTable data-ui-id="table" density="compact" /></div>;
}"""

let private rules code = UiCodeCheck.check spec manifests code |> List.map (fun p -> p.Rule)
let private baseline = Map [ "ui-contract/previews/b/default.png", "aa"; "ui-contract/specs/b.json", "bb" ]

let private report =
    """{"testResults":[{"assertionResults":[
        {"title":"default aria","status":"passed","failureMessages":[]},
        {"title":"empty pixels","status":"failed","failureMessages":["Error: screenshot differs ui__bordereaux__empty"]},
        {"title":"empty placements","status":"failed","failureMessages":["Error: Expected ui__bordereaux__empty__premium to match"]},
        {"title":"error placements","status":"failed","failureMessages":["Error: Placement 'hdr' has no element with data-ui-id=\"hdr\" in state 'error'. x"]},
        {"title":"error aria","status":"failed","failureMessages":["AssertionError: tree differs"]}]}]}"""

let private el id name = { Id = ElementId id; Name = name; CodeAreas = []; Risk = Low; InScope = true; Repo = None }
let private tk id elements writes =
    { Id = TaskId id; Title = ""; Instructions = ""; Elements = elements |> List.map ElementId; Writes = writes |> List.map PathPattern.createOrFail
      TestWrites = []; DependsOn = []; Covers = []; Verify = [] }

let tests : (string * (unit -> unit)) list =
    [ "ui blame: tasks that link the component's element or write its file", fun () ->
          let els = [ el "E1" "BordereauxList"; el "E2" "Other" ]
          let ts = [ tk "T-1" [ "E1" ] []; tk "T-2" [ "E2" ] [ "src/BordereauxList.tsx" ]; tk "T-3" [ "E2" ] [ "src/other/**" ] ]
          let element, blamed = UiBlame.tasks els ts "BordereauxList" "src/BordereauxList.tsx"
          equal (Some "E1") element "element"
          equal [ TaskId "T-1"; TaskId "T-2" ] blamed "blamed"
          equal [] (snd (UiBlame.tasks els ts "Nobody" "src/none.tsx")) "none"
      "ui report: a failure under another screen's describe is not this screen's", fun () ->
          let r = """{"testResults":[{"assertionResults":[
              {"title":"empty pixels","ancestorTitles":["other page"],"status":"failed","failureMessages":["x"]},
              {"title":"empty pixels","ancestorTitles":["bordereaux page"],"status":"passed","failureMessages":[]}]}]}"""
          equal [] (UiReport.failures "bordereaux" r) "none"
          equal 1 (UiReport.executed "bordereaux" r) "executed"
      "integration: a could_not_run ui check is the environment's, a failed one is the code's", fun () ->
          let o code tail = { Name = "ui:b"; ExitCode = code; DurationMs = 1L; OutputTail = tail }
          check (IntegrationRun.classify [ o 3 "could_not_run: page pixels need the runner image" ] = IntegrationReading.Unrunnable [ "ui:b" ]) "unrunnable"
          check (IntegrationRun.classify [ o 1 "placement-pixels in empty" ] = IntegrationReading.CodeFailures) "code"
      "ui code check: the kit's good page passes", fun () -> equal [] (rules good) "no problems"
      "ui code check: a dropped data-ui-id is placement-missing", fun () ->
          equal [ "placement-missing" ] (rules (good.Replace("data-ui-id=\"hdr\" ", ""))) "rule"
      "ui code check: a placement on the wrong component", fun () ->
          equal [ "placement-component" ] (rules (good.Replace("<DataTable data-ui-id=\"table\"", "<PageHeader data-ui-id=\"table\"").Replace("DataTable }", "DataTable }"))) "rule"
      "ui code check: duplicate and unspecified ids", fun () ->
          let r = rules (good.Replace("data-ui-id=\"table\"", "data-ui-id=\"hdr\""))
          check (List.contains "duplicate-id" r && List.contains "placement-missing" r) "dup + missing"
          check (List.contains "unspecified-id" (rules (good.Replace("</div>", "<PageHeader data-ui-id=\"x\" /></div>")))) "unspecified"
      "ui code check: inline style fails", fun () ->
          check (List.contains "inline-style" (rules (good.Replace("<div>", "<div style={{ color: \"red\" }}>")))) "inline-style"
      "ui code check: raw hex and px fail", fun () ->
          check (List.contains "raw-value" (rules (good.Replace("\"compact\"", "\"#ff0000\"")))) "hex"
          check (List.contains "raw-value" (rules (good.Replace("\"compact\"", "\"12px\"")))) "px"
      "ui code check: raw elements and foreign imports fail", fun () ->
          check (List.contains "raw-element" (rules (good.Replace("<div>", "<div><button>x</button>")))) "raw-element"
          check (List.contains "import" (rules (good.Replace("\"@your-org/ui\"", "\"other-lib\"")))) "import"
          check (List.contains "uncatalogued" (rules (good.Replace("{ PageHeader, DataTable }", "{ PageHeader, DataTable, Toast }").Replace("<div>", "<div><Toast />")))) "uncatalogued"
      "ui baseline: untouched is no drift", fun () -> equal [] (UiBaseline.drift baseline baseline) "none"
      "ui baseline: modified, missing and added files drift", fun () ->
          let now = baseline |> Map.add "ui-contract/previews/b/default.png" "zz" |> Map.remove "ui-contract/specs/b.json" |> Map.add "ui-contract/previews/b/placements/x.png" "cc"
          let d = UiBaseline.drift baseline now
          equal 3 d.Length "three lines"
          check (d |> List.exists (fun l -> l.StartsWith "modified")) "modified"
          check (d |> List.exists (fun l -> l.StartsWith "missing")) "missing"
          check (d |> List.exists (fun l -> l.StartsWith "added")) "added"
      "ui baseline: an added file outside previews is not drift", fun () ->
          equal [] (UiBaseline.drift baseline (baseline |> Map.add "src/new.tsx" "q")) "ignored"
      "ui page test: literal test titles per state, no pageTests call", fun () ->
          let t = UiPageTest.text "bordereaux" "BordereauxList" "src/BordereauxList.tsx" [ "default"; "empty" ]
          check (t.Contains "import { BordereauxList as Page } from \"../../src/BordereauxList\";") "import"
          for s in [ "default"; "empty" ] do
              for k in [ "aria"; "pixels"; "placements" ] do check (t.Contains $"test(\"{s} {k}\"") $"{s} {k}"
          check (not (t.Contains "pageTests(")) "no pageTests"
          check (not (t.Contains "error aria")) "only given states"
      "ui report: failures map to kinds, states and placements", fun () ->
          let fs = UiReport.failures "bordereaux" report
          equal 4 fs.Length "four failures"
          let kind st k = fs |> List.find (fun f -> f.State = st && f.Kind = k)
          equal None (kind "empty" "pixels").Placement "pixels no placement"
          equal (Some "premium") (kind "empty" "placement-pixels").Placement "placement"
          equal (Some "hdr") (kind "error" "placement-missing").Placement "missing placement"
          check (fs |> List.exists (fun f -> f.State = "error" && f.Kind = "aria")) "aria" ]
