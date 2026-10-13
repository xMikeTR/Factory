module Factory.Core.Tests.UiAcceptanceTests

open Factory.Core
open Factory.Core.Tests.Support

let private p = PathPattern.createOrFail
let private el id name = { Id = ElementId id; Name = name; CodeAreas = [ p "src/**" ]; Risk = Low; InScope = true }
let private elements = [ el "Pricing/Quote Form" "Quote Form"; el "Pricing/Quote API" "Quote API" ]
let private task id els =
    { Id = TaskId id; Title = ""; Instructions = ""; Elements = els |> List.map ElementId; Writes = []; TestWrites = []
      DependsOn = []; Covers = []; Verify = [] }
let private spec = "tests/acceptance/quote-form/AC-0107.spec.ts"
let private frozen = [ { Path = spec; Hash = "aaaa" } ]
let private failing = match FailingCheck.create spec with Ok f -> f | Error e -> failwith e
let private bug = { Id = "BUG-1"; Summary = "premium lost on 500" }
let private gate = { Gate = "AC-0107 error state"; Strengthens = None }
let private origin = TranslationOrigin(TaskId "T-001")

let tests: (string * (unit -> unit)) list =
    [ "acceptance: identical specs have no drift", fun () ->
          equal [] (AcceptanceSpecs.drift frozen [ spec, "aaaa" ]) "drift"
      "acceptance: a changed, missing or added spec is a hard fail", fun () ->
          equal [ ModifiedSpec spec ] (AcceptanceSpecs.drift frozen [ spec, "bbbb" ]) "modified"
          equal [ MissingSpec spec ] (AcceptanceSpecs.drift frozen []) "missing"
          equal [ AddedSpec "tests/acceptance/x/AC-1.spec.ts" ] (AcceptanceSpecs.drift frozen [ spec, "aaaa"; "tests/acceptance/x/AC-1.spec.ts", "c" ]) "added"
      "acceptance: a failing spec resolves to its AC and element by directory slug", fun () ->
          match AcceptanceSpecs.failure elements spec with
          | Some f ->
              equal "AC-0107" f.Ac "ac"
              equal (Some(ElementId "Pricing/Quote Form")) (f.Element |> Option.map (fun e -> e.Id)) "element"
          | None -> failwith "not parsed"
          equal None (AcceptanceSpecs.failure elements "src/a.spec.ts") "outside tests/acceptance"
      "acceptance: a failing spec is a translation error on each task that changes its element only", fun () ->
          let tasks = [ task "T-001" [ "Pricing/Quote Form" ]; task "T-002" [ "Pricing/Quote API" ]; task "T-003" [ "Pricing/Quote Form"; "Pricing/Quote API" ] ]
          let f = (AcceptanceSpecs.failure elements spec).Value
          equal [ TranslationError(TaskId "T-001"); TranslationError(TaskId "T-003") ] (AcceptanceSpecs.translationErrors tasks f) "tasks"
      "defect: a bug cannot be classified before it is reproduced", fun () ->
          check (Defect.report bug |> Defect.classify origin |> Result.isError) "classify first"
          check (FailingCheck.create "src/Quote.spec.ts" |> Result.isError) "not an acceptance spec"
      "defect: closing needs classification and a new gate", fun () ->
          check (Defect.report bug |> Defect.close gate |> Result.isError) "close reported"
          let closed =
              Defect.report bug
              |> Defect.reproduce failing
              |> Result.bind (Defect.classify origin)
              |> Result.bind (Defect.close gate)
          check (closed |> Result.map Defect.isClosed = Ok true) $"%A{closed}"
          check (closed |> Result.map Defect.originOf = Ok(Some origin)) "origin kept" ]
