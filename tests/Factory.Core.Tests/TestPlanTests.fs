module Factory.Core.Tests.TestPlanTests

open Factory.Core
open Factory.Core.Tests.Support

let private t kind path cmd via : NamedTest = { Kind = kind; Path = path; Command = cmd; Via = via }

let private task (elements: string list) : PlannedTask =
    { Id = TaskId "T-1"; Title = ""; Instructions = ""; Elements = elements |> List.map ElementId; Writes = []; TestWrites = []
      DependsOn = []; Covers = []; Verify = [] }

let private dotnet = t "dotnet" "tests/Api.Tests/Api.Tests.csproj" (Some "dotnet test tests/Api.Tests/Api.Tests.csproj") (Some "project-ref")
let private vitest = t "vitest" "tests/format.test.ts" (Some "npx vitest run tests/format.test.ts") (Some "import")
let private acceptance = t "acceptance" "tests/acceptance/p/AC-1.spec.ts" (Some "npx playwright test tests/acceptance/p/AC-1.spec.ts") None
let private node = t "node" "tests/x.test.js" None (Some "co-located")

let private map: TestMap =
    { Elements =
        [ { Element = "Api"; Repo = Some "Api"; FailToPass = []; PassToPass = [ dotnet ] }
          { Element = "Portal"; Repo = Some "Ui"; FailToPass = [ acceptance ]; PassToPass = [ vitest; node ] }
          { Element = "Portal2"; Repo = Some "Ui"; FailToPass = []; PassToPass = [ vitest ] }
          { Element = "Engine"; Repo = None; FailToPass = []; PassToPass = [] } ]
      Unmapped = [ "Engine", "no existing test references its code" ] }

let tests: (string * (unit -> unit)) list =
    [ "test plan: a task's tests are the union over its elements, deduplicated by path", fun () ->
          let r = TestPlan.forTask None map (task [ "Portal"; "Portal2" ])
          equal [ acceptance ] r.FailToPass "f2p"
          equal [ vitest; node ] r.PassToPass "p2p once"
      "test plan: an element the map does not list gives nothing, and no map entry for another repo's element", fun () ->
          equal true (TestPlan.isEmpty (TestPlan.forTask None map (task [ "Nope" ]))) "unknown"
          equal [ dotnet ] (TestPlan.forTask (Some "Api") map (task [ "Api"; "Portal" ])).PassToPass "split: the Api task keeps Api's tests"
          equal [] (TestPlan.forTask (Some "Api") map (task [ "Portal" ])).FailToPass "split: Ui entry is not an Api task's"
      "test plan: the brief section lists F2P, P2P and the exact commands once each", fun () ->
          let s = TestPlan.briefSection (TestPlan.forTask None map (task [ "Portal"; "Api" ])) |> Option.get
          let expected =
              String.concat "\n"
                  [ "## Tests for this task"
                    "Must pass at integration (frozen; do not edit):"
                    "- `tests/acceptance/p/AC-1.spec.ts`"
                    "Already passing; keep them passing:"
                    "- `tests/Api.Tests/Api.Tests.csproj`"
                    "- `tests/format.test.ts`"
                    "- `tests/x.test.js`"
                    "Run before declare_done:"
                    "npx playwright test tests/acceptance/p/AC-1.spec.ts"
                    "dotnet test tests/Api.Tests/Api.Tests.csproj"
                    "npx vitest run tests/format.test.ts" ]
          equal expected s "section"
      "test plan: an unmapped element is said plainly; a task with nothing to say has no section", fun () ->
          let s = TestPlan.briefSection (TestPlan.forTask None map (task [ "Engine" ])) |> Option.get
          check (s.Contains "No existing test covers Engine; the tests you write are its only cover.") "unmapped line"
          check (not (s.Contains "Run before declare_done")) "no commands"
          equal None (TestPlan.briefSection (TestPlan.forTask None map (task [ "Nope" ]))) "none"
      "test plan: commands become one shell line that runs all of them and fails if any failed", fun () ->
          equal "s=0; a || s=1; b || s=1; exit $s" (TestPlan.shellLine [ "a"; "b" ]) "line" ]
