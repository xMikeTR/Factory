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

let private testProject = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" /></ItemGroup></Project>"""
let private library = """<Project Sdk="Microsoft.NET.Sdk"></Project>"""

/// A real output of dotnet-affected 7.0.1 (--from main --format json --dry-run), recorded 17 Nov 2026 in a repo where src/Lib changed.
let private recordedDotnet = "DRY-RUN: WRITE /tmp/repo/affected.json\nDRY-RUN: CONTENTS:\n[\n  {\n    \"Name\": \"Lib\",\n    \"FilePath\": \"/tmp/repo/src/Lib/Lib.csproj\"\n  },\n  {\n    \"Name\": \"Lib.Tests\",\n    \"FilePath\": \"/tmp/repo/tests/Lib.Tests/Lib.Tests.csproj\"\n  }\n]\n"

let affectedTests: (string * (unit -> unit)) list =
    [ "affected: the dotnet command assumes the owning projects changed (no git needed in the container) and prints JSON to stdout", fun () ->
          equal "dotnet-affected --assume-changes 'src/Lib/Lib.csproj' --assume-changes 'src/Other/Other.csproj' --format json --dry-run"
              (Affected.dotnetCommand [ "src/Lib/Lib.csproj"; "src/Other/Other.csproj" ]) "command"
      "affected: a changed file belongs to the nearest project above it, a project file to itself, a stray file to none", fun () ->
          let projects = [ "src/Lib/Lib.csproj"; "src/Lib/Sub/Sub.csproj"; "tests/T/T.csproj" ]
          equal [ "src/Lib/Lib.csproj"; "src/Lib/Sub/Sub.csproj"; "tests/T/T.csproj" ]
              (Affected.owningProjects projects [ "src/Lib/A.cs"; "src/Lib/Sub/B.cs"; "src/Lib/Sub/C.cs"; "tests/T/T.csproj"; "README.md"; "web/x.ts" ]) "owners"
          equal [] (Affected.owningProjects projects [ "tests/T/new.csproj" ]) "unknown project file"
      "affected: the recorded dotnet-affected output parses to project paths; exit 166 is nothing, any other failure is an error", fun () ->
          equal (Ok [ "/tmp/repo/src/Lib/Lib.csproj"; "/tmp/repo/tests/Lib.Tests/Lib.Tests.csproj" ]) (Affected.parseDotnet 0 recordedDotnet) "recorded"
          equal (Ok []) (Affected.parseDotnet 166 "No affected projects where found for the current changes") "none"
          check (Result.isError (Affected.parseDotnet 127 "")) "tool missing"
          check (Result.isError (Affected.parseDotnet 0 "DRY-RUN: nothing")) "no array"
          check (Result.isError (Affected.parseDotnet 0 "[ {\"x\":1} ]")) "wrong shape"
      "affected: only test projects become `dotnet test` commands, paths relative to the repo", fun () ->
          equal (Some "tests/Lib.Tests/Lib.Tests.csproj") (Affected.relativeTo "/tmp/repo" "/tmp/repo/tests/Lib.Tests/Lib.Tests.csproj") "relative"
          equal None (Affected.relativeTo "/tmp/repo" "/elsewhere/x.csproj") "outside"
          equal [ "tests/Lib.Tests/Lib.Tests.csproj", "dotnet test 'tests/Lib.Tests/Lib.Tests.csproj'" ]
              (Affected.dotnetTests [ "src/Lib/Lib.csproj", library; "tests/Lib.Tests/Lib.Tests.csproj", testProject ]) "tests only"
      "affected: vitest related goes to the nearest package.json that lists vitest, script files only", fun () ->
          let withVitest = """{"devDependencies":{"vitest":"5.1.0"}}"""
          let without = """{"dependencies":{"react":"19"}}"""
          let r = Affected.vitestRelated [ "package.json", without; "web/package.json", withVitest ] [ "web/src/a.ts"; "web/src/b.tsx"; "web/readme.md"; "src/x.ts"; "web/src/a.ts" ]
          equal [ "web", [ "src/a.ts"; "src/b.tsx" ], "cd 'web' && npx vitest related --run --passWithNoTests 'src/a.ts' 'src/b.tsx'" ] r "web only"
          let root = Affected.vitestRelated [ "package.json", withVitest ] [ "src/x.js" ]
          equal [ "", [ "src/x.js" ], "npx vitest related --run --passWithNoTests 'src/x.js'" ] root "root"
          equal [] (Affected.vitestRelated [ "package.json", without ] [ "src/x.ts" ]) "no vitest" ]
