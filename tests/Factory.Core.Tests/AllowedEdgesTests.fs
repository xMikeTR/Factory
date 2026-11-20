module Factory.Core.Tests.AllowedEdgesTests

open Factory.Core
open Factory.Core.Tests.Support

let private examples = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "fares-v3")
let private baseHandover =
    match Json.parseHandover (System.IO.File.ReadAllText(System.IO.Path.Combine(examples, "handover.json"))) with
    | Ok h -> h
    | Error es -> failwithf "%A" es

let private el id area repo =
    { Id = ElementId id; Name = id; CodeAreas = [ PathPattern.createOrFail area ]; Risk = Low; InScope = true; Repo = repo }
let private edge a b = { Source = a; Target = b }
let private rel a b = { From = ElementId a; To = ElementId b }
let private handover elements rels deps = { baseHandover with Elements = elements; Relationships = rels; Dependencies = deps }
let private allow e a = { Element = ElementId e; Allows = a |> List.map ElementId }
let private pairs (vs: AllowViolation list) = vs |> List.map (fun v -> v.FromElement.Value, v.ToElement.Value)

// designc's example (docs/handover-dependencies.example.json): Web (repo Ui) may use both API elements, Ctl -> Repo -> Db.
let private example =
    handover
        [ el "S/Api/Ctl" "api/ctl/**" None; el "S/Api/Repo" "api/repo/**" None; el "S/Db" "db/**" None; el "S/Web" "web/**" (Some "Ui") ]
        []
        (Some [ allow "S/Api/Ctl" [ "S/Api/Repo" ]; allow "S/Api/Repo" [ "S/Db" ]; allow "S/Db" []; allow "S/Web" [ "S/Api/Ctl"; "S/Api/Repo" ] ])

let tests: (string * (unit -> unit)) list =
    [ "allowed edges: a code edge between two elements must be a modelled relationship, in that direction", fun () ->
          let elements = [ el "api" "src/Api/**" None; el "surcharge" "src/Surcharge/**" None; el "db" "src/Db/**" None ]
          let h = handover elements [ rel "api" "surcharge" ] None
          let vs es = AllowedEdges.violations h None ProjectReference es |> pairs
          equal [] (vs [ edge "src/Api/Api.csproj" "src/Surcharge/Surcharge.csproj" ]) "modelled"
          equal [ "surcharge", "api" ] (vs [ edge "src/Surcharge/Surcharge.csproj" "src/Api/Api.csproj" ]) "the model says api -> surcharge, not back"
          equal [ "api", "db" ] (vs [ edge "src/Api/Api.csproj" "src/Db/Db.csproj" ]) "no relationship"
          equal [] (vs [ edge "src/Api/Api.csproj" "src/Api/Api.Tests.csproj" ]) "inside one element"
          equal [] (vs [ edge "src/Api/Api.csproj" "src/Shared/Shared.csproj" ]) "unowned code is not judged"
      "allowed edges: allows comes from dependencies.json when the handover has one, else from the relationships", fun () ->
          let elements = [ el "a" "a/**" None; el "b" "b/**" None ]
          let withDeps = handover elements [ rel "a" "b" ] (Some [ allow "a" []; allow "b" [ "a" ] ])
          equal (Map.ofList [ ElementId "a", Set.empty; ElementId "b", Set.ofList [ ElementId "a" ] ]) (AllowedEdges.allows withDeps) "dependencies win"
          equal (Map.ofList [ ElementId "a", Set.ofList [ ElementId "b" ] ]) (AllowedEdges.allows (handover elements [ rel "a" "b" ] None)) "fallback"
      "allowed edges: a rel drawn above the code is lifted by dependencies.json, and the same rels alone allow nothing", fun () ->
          let vs h es = AllowedEdges.violations h None Import es |> pairs
          equal [] (vs example [ edge "web/app.ts" "api/repo/x.ts" ]) "S/Web imports api/repo"
          equal [ "S/Db", "S/Web" ] (vs example [ edge "db/a.ts" "web/a.ts" ]) "S/Db -> S/Web is not allowed"
          let plain = { example with Dependencies = None }
          equal [ "S/Web", "S/Api/Repo" ] (vs plain [ edge "web/app.ts" "api/repo/x.ts" ]) "without the lift the container rel allows nothing"
      "allowed edges: owner looks only at the task's repo", fun () ->
          let elements = [ el "ApiSrc" "src/**" (Some "Api"); el "UiSrc" "src/**" (Some "Ui") ]
          equal (Some "UiSrc") (AllowedEdges.owner elements (Some "Ui") "src/a.ts" |> Option.map (fun e -> e.Id.Value)) "Ui"
          equal (Some "ApiSrc") (AllowedEdges.owner elements (Some "Api") "src/a.ts" |> Option.map (fun e -> e.Id.Value)) "Api"
          equal (Some "ApiSrc") (AllowedEdges.owner elements None "src/a.ts" |> Option.map (fun e -> e.Id.Value)) "no repo: first match"
          equal None (AllowedEdges.owner elements (Some "Other") "src/a.ts" |> Option.map (fun e -> e.Id.Value)) "unknown repo"
      "allowed edges: only element pairs the baseline did not already cross are new, and debt is by pair across kinds", fun () ->
          let v kind a b src = { Kind = kind; Edge = edge src "y"; FromElement = ElementId a; ToElement = ElementId b }
          let isNew = AllowedEdges.newSince [ v ProjectReference "api" "db" "a" ] [ v Import "api" "db" "b"; v Import "surcharge" "db" "c" ] |> pairs
          equal [ "surcharge", "db" ] isNew "api->db is debt whatever the kind"
          let debt = AllowedEdges.debt [ v ProjectReference "api" "db" "a"; v ProjectReference "web" "db" "w" ] [ v AssemblyUse "api" "db" "a"; v Import "api" "db" "b"; v Import "surcharge" "db" "r" ]
          equal [ ElementId "api", ElementId "db" ] debt "once, and only the pair the baseline crossed"
      "allowed edges: the line names the missing relationship for each kind", fun () ->
          let v kind = { Kind = kind; Edge = edge "s" "t"; FromElement = ElementId "A"; ToElement = ElementId "B" }
          for kind, verb in [ ProjectReference, "references"; AssemblyUse, "uses types from"; Import, "imports" ] do
              let l = AllowedEdges.line (v kind)
              check (l.StartsWith $"- s {verb} t: ") $"verb {verb}: {l}"
              check (l.Contains "no relationship A → B") $"names the rel: {l}"
      "allowed edges: a task's repo is the single repo of its elements", fun () ->
          let elements = [ el "a" "a/**" (Some "Api"); el "b" "b/**" (Some "Ui"); el "c" "c/**" None ]
          let task els = { Id = TaskId "T"; Title = ""; Instructions = ""; Elements = els |> List.map ElementId; Writes = []; TestWrites = []; DependsOn = []; Covers = []; Verify = [] }
          equal (Some "Api") (AllowedEdges.taskRepo elements (task [ "a" ])) "one"
          equal None (AllowedEdges.taskRepo elements (task [ "a"; "b" ])) "two repos"
          equal None (AllowedEdges.taskRepo elements (task [ "c" ])) "none" ]
