namespace Factory.Core

open System
open System.Text.Json
open System.Text.Json.Nodes

/// Decoders return every problem they find, so a malformed plan from the planner agent gets
/// one complete list of fixes rather than one error per retry.
[<RequireQualifiedAccess>]
module Json =

    exception private DecodeFailure of string

    let private fail msg = raise (DecodeFailure msg)

    let private obj (path: string) (n: JsonNode) =
        match n with
        | :? JsonObject as o -> o
        | _ -> fail $"{path}: expected an object"

    let private str (path: string) (o: JsonObject) (key: string) =
        match o.[key] with
        | null -> fail $"{path}.{key}: required"
        | v ->
            try v.GetValue<string>() with _ -> fail $"{path}.{key}: expected a string"

    let private optStr (o: JsonObject) (key: string) =
        match o.[key] with
        | null -> None
        | v -> (try Some(v.GetValue<string>()) with _ -> None) |> Option.filter (String.IsNullOrWhiteSpace >> not)

    /// A handover frozen before item handovers has no `inScope`: every element is in scope.
    let private inScope (o: JsonObject) =
        match o.["inScope"] with
        | null -> true
        | v -> (try v.GetValue<bool>() with _ -> true)

    let private int (path: string) (o: JsonObject) (key: string) (fallback: int) =
        match o.[key] with
        | null -> fallback
        | v -> try v.GetValue<int>() with _ -> fail $"{path}.{key}: expected an integer"

    let private arr (path: string) (o: JsonObject) (key: string) : (string * JsonNode) list =
        match o.[key] with
        | null -> []
        | :? JsonArray as a -> a |> Seq.mapi (fun i n -> $"{path}.{key}[{i}]", n) |> List.ofSeq
        | _ -> fail $"{path}.{key}: expected an array"

    let private strs path o key =
        arr path o key
        |> List.map (fun (p, n) -> try n.GetValue<string>() with _ -> fail $"{p}: expected a string")

    let private pattern path (s: string) =
        match PathPattern.create s with
        | Ok p -> p
        | Error e -> fail $"{path}: {e}"

    let private risk path =
        function
        | "low" -> Low
        | "medium" -> Medium
        | "high" -> High
        | other -> fail $"{path}: unknown risk '{other}' (low|medium|high)"

    /// Run a decoder that may raise several failures by collecting them per item.
    let private collect (items: (string * JsonNode) list) (decode: string -> JsonNode -> 'a) =
        let results = items |> List.map (fun (p, n) -> try Ok(decode p n) with DecodeFailure m -> Error m)
        let errors = results |> List.choose (function Error e -> Some e | Ok _ -> None)
        if errors.IsEmpty then results |> List.choose (function Ok v -> Some v | Error _ -> None)
        else fail (String.Join("\n", errors))

    let private run (text: string) (decode: JsonObject -> 'a) : Result<'a, string list> =
        try
            match JsonNode.Parse text with
            | null -> Error [ "empty document" ]
            | n -> Ok(decode (obj "$" n))
        with
        | DecodeFailure m -> Error(m.Split('\n') |> List.ofArray)
        | :? JsonException as e -> Error [ $"invalid JSON: {e.Message}" ]
        // A value of the wrong kind where a decoder reads it unguarded (GetValue, AsObject) throws these.
        | :? InvalidOperationException | :? FormatException | :? InvalidCastException as e -> Error [ $"unexpected value: {e.Message}" ]

    // ---------- handover ----------

    let private decodeElement path n =
        let o = obj path n
        { Id = ElementId(str path o "id")
          Name = str path o "name"
          CodeAreas = strs path o "codeAreas" |> List.map (pattern $"{path}.codeAreas")
          Risk = risk $"{path}.risk" (str path o "risk")
          InScope = inScope o
          Repo = optStr o "repo" }

    let private decodeDecision path n =
        let o = obj path n
        { Id = DecisionId(str path o "id")
          Statement = str path o "statement"
          Binds = strs path o "binds" |> List.map ElementId
          Checks =
            arr path o "checks"
            |> List.map (fun (p, c) ->
                let co = obj p c
                // A handover.json written before check ids had none: derive it as designc does.
                DecisionCheck.create (str p co "kind") (str p co "run") (optStr co "id")) }

    let private decodeUiText p n =
        let o = obj p n
        { Path = str p o "path"; Sha256 = str p o "sha256"; Text = str p o "text" }

    let private decodeUiScreen p n =
        let o = obj p n
        { Screen = str p o "screen"
          Component = str p o "component"
          File = str p o "file"
          Spec = decodeUiText $"{p}.spec" (match o.["spec"] with null -> fail $"{p}.spec: required" | v -> v)
          Template = decodeUiText $"{p}.template" (match o.["template"] with null -> fail $"{p}.template: required" | v -> v)
          Components = collect (arr p o "components") decodeUiText
          Previews =
            collect (arr p o "previews") (fun q v ->
                let po = obj q v
                str q po "path", str q po "sha256")
          Holdout = optStr o "holdout"
          HoldoutCases =
            collect (arr p o "holdoutCases") (fun q v ->
                let co = obj q v
                { Criterion = str q co "criterion"; Case = int q co "case" 0; State = str q co "state"; Sha256 = str q co "sha256" }) }

    let private decodeNamedTest p n =
        let o = obj p n
        { Kind = str p o "kind"; Path = str p o "path"; Command = optStr o "command"; Via = optStr o "via" }

    let private decodeTestMap p n =
        let o = obj p n
        { Elements =
            collect (arr p o "elements") (fun q v ->
                let eo = obj q v
                { Element = str q eo "element"
                  Repo = optStr eo "repo"
                  FailToPass = collect (arr q eo "failToPass") decodeNamedTest
                  PassToPass = collect (arr q eo "passToPass") decodeNamedTest })
          Unmapped =
            collect (arr p o "unmapped") (fun q v ->
                let uo = obj q v
                str q uo "element", str q uo "reason") }

    let parseHandover (text: string) : Result<Handover, string list> =
        run text (fun o ->
            { Id = str "$" o "id"
              Digest = str "$" o "digest"
              Elements = collect (arr "$" o "elements") decodeElement
              Decisions = collect (arr "$" o "decisions") decodeDecision
              ProtectedPaths = strs "$" o "protectedPaths" |> List.map (pattern "$.protectedPaths")
              EventVocabulary = strs "$" o "eventVocabulary"
              Relationships =
                collect (arr "$" o "relationships") (fun p n ->
                    let ro = obj p n
                    { From = ElementId(str p ro "from"); To = ElementId(str p ro "to") })
              AcceptanceSpecs =
                collect (arr "$" o "acceptanceSpecs") (fun p n ->
                    let so = obj p n
                    { Path = str p so "path"; Hash = str p so "hash" })
              WorkItem = optStr o "workItem"
              DesignTokens = optStr o "designTokens"
              DesignVersion = optStr o "designVersion"
              AcceptanceCriteria =
                collect (arr "$" o "acceptanceCriteria") (fun p n ->
                    let co = obj p n
                    { Id = str p co "id"; Element = str p co "element"; Text = str p co "text" })
              LoopSpecs = strs "$" o "loopSpecs"
              References =
                collect (arr "$" o "references") (fun p n ->
                    let ro = obj p n
                    { Key = str p ro "key"; Kind = str p ro "kind" })
              Contracts =
                collect (arr "$" o "contracts") (fun p n ->
                    let co = obj p n
                    { Agent = str p co "agent"; Policy = str p co "policy"; Hash = str p co "hash" })
              Uncovered = strs "$" o "uncovered"
              ApiContracts = strs "$" o "apiContracts"
              RequirementRisk =
                (match optStr o "requirementRisk" with
                 | Some "low" -> Some Low
                 | Some "medium" -> Some Medium
                 | Some "high" | Some "critical" -> Some High
                 | _ -> None)
              RequirementDataClass =
                (match optStr o "requirementDataClass" with
                 | Some ("public" | "internal" | "regulated" as d) -> Some d
                 | _ -> None)
              Ui = collect (arr "$" o "ui") decodeUiScreen
              Tests = (match o.["tests"] with null -> None | v -> Some(decodeTestMap "$.tests" v))
              ScenarioTests =
                collect (arr "$" o "scenarioTests") (fun p n ->
                    let so = obj p n
                    { Screen = str p so "screen"
                      Component = str p so "component"
                      Test = decodeUiText $"{p}.test" (match so.["test"] with null -> fail $"{p}.test: required" | v -> v)
                      Scenarios =
                        collect (arr p so "scenarios") (fun q v ->
                            let co = obj q v
                            { Id = str q co "id"
                              Operation = str q co "operationId", str q co "method", str q co "path"
                              Examples = collect (arr q co "examples") (fun r e -> let eo = obj r e in int r eo "index" 0, str r eo "title") }) })
              ScenarioSkipped =
                collect (arr "$" o "scenarioSkipped") (fun p n -> let so = obj p n in str p so "scenario", str p so "reason")
              Interfaces =
                collect (arr "$" o "interfaces") (fun p n ->
                    let io = obj p n
                    { Element = str p io "element"
                      Role = str p io "role"
                      From = str p io "from"
                      To = str p io "to"
                      Kind = str p io "kind"
                      Id = str p io "id"
                      Method = optStr io "method"
                      Path = optStr io "path"
                      File = optStr io "file"
                      Version = str p io "version" })
              ApiContractTexts = collect (arr "$" o "apiContractTexts") (fun p n -> decodeUiText p n)
              Dependencies =
                (match o.["dependencies"] with
                 | null -> None
                 | _ ->
                     Some(
                         collect (arr "$" o "dependencies") (fun p n ->
                             let eo = obj p n
                             { Element = ElementId(str p eo "element")
                               Allows = strs p eo "allows" |> List.map ElementId })))

              ApiScenarios =
                collect (arr "$" o "apiScenarios") (fun p n ->
                    let ao = obj p n
                    let rowSet q (v: JsonNode) =
                        let go = obj q v
                        str q go "entity", collect (arr q go "rows") (fun _ x -> x.ToJsonString())
                    { Id = str p ao "id"
                      Path = str p ao "path"
                      Sha256 = str p ao "sha256"
                      OperationId = str p ao "operationId"
                      Method = str p ao "method"
                      OpPath = str p ao "opPath"
                      Examples =
                        collect (arr p ao "examples") (fun q e ->
                            let eo = obj q e
                            { Index = int q eo "index" 0
                              Title = str q eo "title"
                              Given = collect (arr q eo "given") rowSet
                              Then = collect (arr q eo "then") rowSet
                              Request =
                                (match eo.["request"] with
                                 | null -> None
                                 | rq ->
                                     let ro = obj $"{q}.request" rq
                                     Some { Body = optStr ro "body"
                                            Path = collect (arr q ro "path") (fun r x -> let xo = obj r x in str r xo "name", str r xo "value") }) }) })
              Entities =
                collect (arr "$" o "entities") (fun p n ->
                    let eo = obj p n
                    { Id = str p eo "id"
                      Pack = str p eo "pack"
                      Name = str p eo "name"
                      Version = int p eo "version" 1
                      Fields = collect (arr p eo "fields") (fun q f -> let fo = obj q f in str q fo "name", str q fo "type") }) })

    // ---------- plan ----------

    let private decodeVerify path n =
        let o = obj path n
        let name = str path o "name"
        match str path o "kind" with
        | "manual" -> Manual { Name = name; Instructions = str path o "instructions" }
        | k ->
            let kind =
                match k with
                | "invariant" -> Invariant
                | "self-seeded" -> SelfSeeded
                | other -> fail $"{path}.kind: unknown '{other}' (invariant|self-seeded|manual)"
            Automated
                { Name = name
                  Kind = kind
                  Run = str path o "run"
                  Cleanup = optStr o "cleanup"
                  TimeoutSeconds = int path o "timeoutSeconds" 300 }

    let private decodeTask path n =
        let o = obj path n
        { Id = TaskId(str path o "id")
          Title = str path o "title"
          Instructions = str path o "instructions"
          Elements = strs path o "elements" |> List.map ElementId
          Writes = strs path o "writes" |> List.map (pattern $"{path}.writes")
          TestWrites = strs path o "testWrites" |> List.map (pattern $"{path}.testWrites")
          DependsOn = strs path o "dependsOn" |> List.map TaskId
          Covers = strs path o "covers" |> List.map DecisionId
          Verify = collect (arr path o "verify") decodeVerify }

    let parsePlan (text: string) : Result<Plan, string list> =
        run text (fun o ->
            let h = obj "$.handover" (match o.["handover"] with null -> fail "$.handover: required" | n -> n)
            { HandoverId = str "$.handover" h "id"
              HandoverDigest = str "$.handover" h "digest"
              Tasks = collect (arr "$" o "tasks") decodeTask })

    // ---------- dispatch plan (written to dispatch.json for the runner and for humans) ----------

    let private strArr (xs: string seq) = JsonArray(xs |> Seq.map (fun s -> JsonValue.Create s :> JsonNode) |> Array.ofSeq)

    let writeDispatch (d: DispatchPlan) : string =
        let waves =
            d.Waves
            |> List.map (fun w ->
                let teams =
                    w.Teams
                    |> List.map (fun t ->
                        JsonObject(
                            [ Collections.Generic.KeyValuePair<string, JsonNode>("id", JsonValue.Create t.Id.Value)
                              Collections.Generic.KeyValuePair<string, JsonNode>("tasks", strArr (t.Tasks |> List.map (fun x -> x.Id.Value)))
                              Collections.Generic.KeyValuePair<string, JsonNode>("writes", strArr (t.Writes |> List.map (fun p -> p.Value)))
                              Collections.Generic.KeyValuePair<string, JsonNode>("elements", strArr (t.Elements |> List.map (fun e -> e.Value))) ]
                        )
                        :> JsonNode)
                    |> Array.ofList
                JsonObject(
                    [ Collections.Generic.KeyValuePair<string, JsonNode>("index", JsonValue.Create w.Index)
                      Collections.Generic.KeyValuePair<string, JsonNode>("teams", JsonArray(teams)) ]
                )
                :> JsonNode)
            |> Array.ofList
        let root =
            JsonObject(
                [ Collections.Generic.KeyValuePair<string, JsonNode>("handover", JsonValue.Create d.HandoverId)
                  Collections.Generic.KeyValuePair<string, JsonNode>("waves", JsonArray(waves)) ]
            )
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let private node (pairs: (string * JsonNode) list) : JsonNode =
        JsonObject(pairs |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair<string, JsonNode>(k, v))) :> JsonNode

    let private riskName = function Low -> "low" | Medium -> "medium" | High -> "high"

    /// Inverse of parseHandover: how a handover read from Designc is frozen into a run directory.
    let writeHandover (h: Handover) : string =
        let element (e: ElementSpec) =
            node [ "id", JsonValue.Create e.Id.Value
                   "name", JsonValue.Create e.Name
                   "codeAreas", strArr (e.CodeAreas |> List.map (fun p -> p.Value))
                   "risk", JsonValue.Create(riskName e.Risk)
                   "inScope", JsonValue.Create e.InScope
                   match e.Repo with Some r -> "repo", JsonValue.Create r | None -> () ]
        let decision (d: BoundDecision) =
            node [ "id", JsonValue.Create d.Id.Value
                   "statement", JsonValue.Create d.Statement
                   "binds", strArr (d.Binds |> List.map (fun b -> b.Value))
                   "checks", JsonArray(d.Checks |> List.map (fun c -> node [ "kind", JsonValue.Create c.Kind; "run", JsonValue.Create c.Run; "id", JsonValue.Create c.CheckId ]) |> Array.ofList) ]
        let root =
            node [ "id", JsonValue.Create h.Id
                   "digest", JsonValue.Create h.Digest
                   "elements", JsonArray(h.Elements |> List.map element |> Array.ofList)
                   "decisions", JsonArray(h.Decisions |> List.map decision |> Array.ofList)
                   "protectedPaths", strArr (h.ProtectedPaths |> List.map (fun p -> p.Value))
                   "eventVocabulary", strArr h.EventVocabulary
                   "relationships", JsonArray(h.Relationships |> List.map (fun r -> node [ "from", JsonValue.Create r.From.Value; "to", JsonValue.Create r.To.Value ]) |> Array.ofList)
                   "acceptanceSpecs", JsonArray(h.AcceptanceSpecs |> List.map (fun a -> node [ "path", JsonValue.Create a.Path; "hash", JsonValue.Create a.Hash ]) |> Array.ofList)
                   if h.WorkItem.IsSome then "workItem", JsonValue.Create h.WorkItem.Value
                   if h.DesignTokens.IsSome then "designTokens", JsonValue.Create h.DesignTokens.Value
                   if h.DesignVersion.IsSome then "designVersion", JsonValue.Create h.DesignVersion.Value
                   if not h.AcceptanceCriteria.IsEmpty then
                       "acceptanceCriteria",
                       JsonArray(h.AcceptanceCriteria |> List.map (fun c -> node [ "id", JsonValue.Create c.Id; "element", JsonValue.Create c.Element; "text", JsonValue.Create c.Text ]) |> Array.ofList)
                   if not h.LoopSpecs.IsEmpty then "loopSpecs", strArr h.LoopSpecs
                   if not h.References.IsEmpty then
                       "references", JsonArray(h.References |> List.map (fun r -> node [ "key", JsonValue.Create r.Key; "kind", JsonValue.Create r.Kind ]) |> Array.ofList)
                   if not h.Contracts.IsEmpty then
                       "contracts", JsonArray(h.Contracts |> List.map (fun c -> node [ "agent", JsonValue.Create c.Agent; "policy", JsonValue.Create c.Policy; "hash", JsonValue.Create c.Hash ]) |> Array.ofList)
                   if not h.Uncovered.IsEmpty then "uncovered", strArr h.Uncovered
                   if not h.ApiContracts.IsEmpty then "apiContracts", strArr h.ApiContracts
                   match h.RequirementRisk with
                   | Some r -> "requirementRisk", JsonValue.Create((match r with Low -> "low" | Medium -> "medium" | High -> "high"))
                   | None -> ()
                   match h.RequirementDataClass with
                   | Some d -> "requirementDataClass", JsonValue.Create d
                   | None -> ()
                   if not h.Ui.IsEmpty then
                       let text (t: UiText) = node [ "path", JsonValue.Create t.Path; "sha256", JsonValue.Create t.Sha256; "text", JsonValue.Create t.Text ]
                       let screen (s: UiScreen) =
                           node [ "screen", JsonValue.Create s.Screen
                                  "component", JsonValue.Create s.Component
                                  "file", JsonValue.Create s.File
                                  "spec", text s.Spec
                                  "template", text s.Template
                                  "components", JsonArray(s.Components |> List.map text |> Array.ofList)
                                  "previews", JsonArray(s.Previews |> List.map (fun (p, h) -> node [ "path", JsonValue.Create p; "sha256", JsonValue.Create h ]) |> Array.ofList)
                                  match s.Holdout with Some h -> "holdout", JsonValue.Create h | None -> ()
                                  "holdoutCases",
                                  JsonArray(s.HoldoutCases |> List.map (fun c -> node [ "criterion", JsonValue.Create c.Criterion; "case", JsonValue.Create c.Case; "state", JsonValue.Create c.State; "sha256", JsonValue.Create c.Sha256 ]) |> Array.ofList) ]
                       "ui", JsonArray(h.Ui |> List.map screen |> Array.ofList)
                   match h.Tests with
                   | Some t ->
                       let named (n: NamedTest) =
                           node [ "kind", JsonValue.Create n.Kind
                                  "path", JsonValue.Create n.Path
                                  match n.Command with Some c -> "command", JsonValue.Create c | None -> ()
                                  match n.Via with Some v -> "via", JsonValue.Create v | None -> () ]
                       let element (e: ElementTests) =
                           node [ "element", JsonValue.Create e.Element
                                  match e.Repo with Some r -> "repo", JsonValue.Create r | None -> ()
                                  "failToPass", JsonArray(e.FailToPass |> List.map named |> Array.ofList)
                                  "passToPass", JsonArray(e.PassToPass |> List.map named |> Array.ofList) ]
                       "tests",
                       node [ "elements", JsonArray(t.Elements |> List.map element |> Array.ofList)
                              "unmapped", JsonArray(t.Unmapped |> List.map (fun (e, r) -> node [ "element", JsonValue.Create e; "reason", JsonValue.Create r ]) |> Array.ofList) ]
                   | None -> ()
                   if not h.ScenarioTests.IsEmpty then
                       let text (t: UiText) = node [ "path", JsonValue.Create t.Path; "sha256", JsonValue.Create t.Sha256; "text", JsonValue.Create t.Text ]
                       let scenario (c: ScenarioRef) =
                           let (opId, meth, path) = c.Operation
                           node [ "id", JsonValue.Create c.Id
                                  "operationId", JsonValue.Create opId
                                  "method", JsonValue.Create meth
                                  "path", JsonValue.Create path
                                  "examples", JsonArray(c.Examples |> List.map (fun (i, t) -> node [ "index", JsonValue.Create i; "title", JsonValue.Create t ]) |> Array.ofList) ]
                       let screen (s: ScenarioScreen) =
                           node [ "screen", JsonValue.Create s.Screen
                                  "component", JsonValue.Create s.Component
                                  "test", text s.Test
                                  "scenarios", JsonArray(s.Scenarios |> List.map scenario |> Array.ofList) ]
                       "scenarioTests", JsonArray(h.ScenarioTests |> List.map screen |> Array.ofList)
                   if not h.ScenarioSkipped.IsEmpty then
                       "scenarioSkipped", JsonArray(h.ScenarioSkipped |> List.map (fun (s, r) -> node [ "scenario", JsonValue.Create s; "reason", JsonValue.Create r ]) |> Array.ofList)
                   if not h.Interfaces.IsEmpty then
                       let iface (i: ElementInterface) =
                           node [ "element", JsonValue.Create i.Element
                                  "role", JsonValue.Create i.Role
                                  "from", JsonValue.Create i.From
                                  "to", JsonValue.Create i.To
                                  "kind", JsonValue.Create i.Kind
                                  "id", JsonValue.Create i.Id
                                  match i.Method with Some m -> "method", JsonValue.Create m | None -> ()
                                  match i.Path with Some p -> "path", JsonValue.Create p | None -> ()
                                  match i.File with Some f -> "file", JsonValue.Create f | None -> ()
                                  "version", JsonValue.Create i.Version ]
                       "interfaces", JsonArray(h.Interfaces |> List.map iface |> Array.ofList)
                   match h.Dependencies with
                   | Some deps ->
                       "dependencies",
                       JsonArray(
                           deps
                           |> List.map (fun d ->
                               node [ "element", JsonValue.Create d.Element.Value
                                      "allows", JsonArray(d.Allows |> List.map (fun a -> JsonValue.Create a.Value :> JsonNode) |> Array.ofList) ])
                           |> Array.ofList)
                   | None -> ()
                   if not h.ApiScenarios.IsEmpty then
                       let rowSet (entity: string, rows: string list) =
                           node [ "entity", JsonValue.Create entity; "rows", JsonArray(rows |> List.map (fun r -> JsonNode.Parse r) |> Array.ofList) ]
                       let example (e: ApiExample) : JsonNode =
                           node [ "index", JsonValue.Create e.Index
                                  "title", JsonValue.Create e.Title
                                  "given", JsonArray(e.Given |> List.map rowSet |> Array.ofList)
                                  "then", JsonArray(e.Then |> List.map rowSet |> Array.ofList)
                                  match e.Request with
                                  | Some r ->
                                      "request",
                                      node [ match r.Body with Some b -> "body", JsonValue.Create b | None -> ()
                                             "path", JsonArray(r.Path |> List.map (fun (k, v) -> node [ "name", JsonValue.Create k; "value", JsonValue.Create v ]) |> Array.ofList) ]
                                  | None -> () ]
                       let scenario (a: ApiScenario) =
                           node [ "id", JsonValue.Create a.Id
                                  "path", JsonValue.Create a.Path
                                  "sha256", JsonValue.Create a.Sha256
                                  "operationId", JsonValue.Create a.OperationId
                                  "method", JsonValue.Create a.Method
                                  "opPath", JsonValue.Create a.OpPath
                                  "examples", JsonArray(a.Examples |> List.map example |> Array.ofList) ]
                       "apiScenarios", JsonArray(h.ApiScenarios |> List.map scenario |> Array.ofList)
                   if not h.Entities.IsEmpty then
                       let entity (e: EntityDef) =
                           node [ "id", JsonValue.Create e.Id
                                  "pack", JsonValue.Create e.Pack
                                  "name", JsonValue.Create e.Name
                                  "version", JsonValue.Create e.Version
                                  "fields", JsonArray(e.Fields |> List.map (fun (n, t) -> node [ "name", JsonValue.Create n; "type", JsonValue.Create t ]) |> Array.ofList) ]
                       "entities", JsonArray(h.Entities |> List.map entity |> Array.ofList)
                   if not h.ApiContractTexts.IsEmpty then
                       "apiContractTexts", JsonArray(h.ApiContractTexts |> List.map (fun t -> node [ "path", JsonValue.Create t.Path; "sha256", JsonValue.Create t.Sha256; "text", JsonValue.Create t.Text ]) |> Array.ofList) ]
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    // ---------- task events (the ledger payload a runner replays on resume) ----------

    let private tierName = function Frontier -> "frontier" | Standard -> "standard" | Small -> "small"

    let private tierOf path = function
        | "frontier" -> Frontier
        | "standard" -> Standard
        | "small" -> Small
        | other -> fail $"{path}: unknown tier '{other}'"

    let private outcomesNode (outcomes: VerifyOutcome list) : JsonNode =
        outcomes
        |> List.map (fun v ->
            let x = JsonObject()
            x.["name"] <- JsonValue.Create v.Name
            x.["exitCode"] <- JsonValue.Create v.ExitCode
            x.["durationMs"] <- JsonValue.Create v.DurationMs
            x.["outputTail"] <- JsonValue.Create v.OutputTail
            x :> JsonNode)
        |> Array.ofList
        |> JsonArray
        :> JsonNode

    let private decodeOutcomes (o: JsonObject) (key: string) : VerifyOutcome list =
        arr "$" o key
        |> List.map (fun (p, n) ->
            let x = obj p n
            { Name = str p x "name"
              ExitCode = int p x "exitCode" -1
              DurationMs = (match x.["durationMs"] with null -> 0L | v -> v.GetValue<int64>())
              OutputTail = defaultArg (optStr x "outputTail") "" })

    let private redKindName = function BuildFailure -> "build_failure" | AssertionFailure -> "assertion_failure" | AgainstMutant -> "against_mutant"

    let private redKindOf path = function
        | "build_failure" -> BuildFailure
        | "assertion_failure" -> AssertionFailure
        | "against_mutant" -> AgainstMutant
        | other -> fail $"{path}: unknown red kind '{other}'"

    let private categoryName = function
        | TestGaming -> "test_gaming" | Intent -> "intent" | BoundDecision -> "bound_decision"
        | Security -> "security" | WeakTests -> "weak_tests" | Design -> "design"

    let private categoryOf path = function
        | "test_gaming" -> TestGaming | "intent" -> Intent | "bound_decision" -> BoundDecision
        | "security" -> Security | "weak_tests" -> WeakTests | "design" -> Design
        | other -> fail $"{path}: unknown finding category '{other}'"

    let private independenceName = function Independent -> "independent" | SameFamily -> "same_family"

    let private independenceOf path = function
        | "independent" -> Independent
        | "same_family" -> SameFamily
        | other -> fail $"{path}: unknown independence '{other}'"

    let private findingsNode (fs: Finding list) : JsonNode =
        let arr = JsonArray()
        for f in fs do
            let x = JsonObject()
            x.["category"] <- JsonValue.Create(categoryName f.Category)
            x.["file"] <- JsonValue.Create f.File
            x.["line"] <- JsonValue.Create f.Line
            x.["message"] <- JsonValue.Create f.Message
            arr.Add x
        arr

    let private decodeFindings (o: JsonObject) (key: string) : Finding list =
        arr "$" o key
        |> List.map (fun (p, n) ->
            let x = obj p n
            { Category = categoryOf $"{p}.category" (str p x "category")
              File = str p x "file"
              Line = int p x "line" 0
              Message = str p x "message" })

    let encodeTaskEvent (e: TaskEvent) : string =
        let o = JsonObject()
        let set (k: string) (v: JsonNode) = o.[k] <- v
        match e with
        | TurnStarted t ->
            set "type" (JsonValue.Create "turn_started")
            set "tier" (JsonValue.Create(tierName t))
        | TurnEnded -> set "type" (JsonValue.Create "turn_ended")
        | NeedsDecisionRaised nd ->
            set "type" (JsonValue.Create "needs_decision")
            set "question" (JsonValue.Create nd.Question)
            set "source" (JsonValue.Create nd.Source)
            set "elements" (strArr (nd.Elements |> List.map (fun x -> x.Value)))
            if not nd.Options.IsEmpty then
                let arr = JsonArray()
                for op in nd.Options do
                    let x = JsonObject()
                    x.["id"] <- JsonValue.Create op.Id
                    x.["summary"] <- JsonValue.Create op.Summary
                    x.["consequence"] <- JsonValue.Create op.Consequence
                    arr.Add x
                set "options" arr
            nd.Recommended |> Option.iter (fun r -> set "recommended" (JsonValue.Create r))
        | DecisionBound d ->
            set "type" (JsonValue.Create "decision_bound")
            set "decision" (JsonValue.Create d.Value)
        | VerifyRequested -> set "type" (JsonValue.Create "verify_requested")
        | VerifyCompleted(green, stashed) ->
            set "type" (JsonValue.Create "verify_completed")
            set "outcomes" (outcomesNode green)
            set "stashedImpl" (outcomesNode stashed)
        | TestsAuthored red ->
            set "type" (JsonValue.Create "tests_authored")
            set "kind" (JsonValue.Create(redKindName red.Kind))
            set "outcomes" (outcomesNode red.Outcomes)
        | ReturnedToTestAuthor problem ->
            set "type" (JsonValue.Create "returned_to_test_author")
            set "problem" (JsonValue.Create problem)
        | ReviewCompleted r ->
            set "type" (JsonValue.Create "review_completed")
            set "blocking" (findingsNode r.Blocking)
            set "advisory" (findingsNode r.Advisory)
            set "independence" (JsonValue.Create(independenceName r.Independence))
        | ReviewAccepted(note, independence) ->
            set "type" (JsonValue.Create "review_accepted")
            set "note" (JsonValue.Create note)
            set "independence" (JsonValue.Create(independenceName independence))
        | ReviewGuided guidance ->
            set "type" (JsonValue.Create "review_guided")
            set "guidance" (JsonValue.Create guidance)
        | Abandon r ->
            set "type" (JsonValue.Create "abandon")
            set "reason" (JsonValue.Create r)
        o.ToJsonString()

    let decodeTaskEvent (text: string) : Result<TaskEvent, string list> =
        run text (fun o ->
            match str "$" o "type" with
            | "turn_started" -> TurnStarted(tierOf "$.tier" (str "$" o "tier"))
            | "turn_ended" -> TurnEnded
            | "needs_decision" ->
                NeedsDecisionRaised
                    { Question = str "$" o "question"
                      Source = str "$" o "source"
                      Elements = strs "$" o "elements" |> List.map ElementId
                      Options =
                          match o.["options"] with
                          | :? JsonArray as arr ->
                              [ for i in 0 .. arr.Count - 1 ->
                                    let x = arr.[i].AsObject()
                                    let p = $"$.options[{i}]"
                                    { Id = str p x "id"; Summary = str p x "summary"; Consequence = str p x "consequence" } ]
                          | _ -> []
                      Recommended =
                          match o.["recommended"] with
                          | null -> None
                          | v -> Some(v.GetValue<string>()) }
            | "decision_bound" -> DecisionBound(DecisionId(str "$" o "decision"))
            | "verify_requested" -> VerifyRequested
            | "verify_completed" -> VerifyCompleted(decodeOutcomes o "outcomes", decodeOutcomes o "stashedImpl")
            | "tests_authored" ->
                let outcomes = decodeOutcomes o "outcomes"
                // A frozen AgainstMutant record was accepted with at least one kill; the count is not kept.
                let created =
                    match redKindOf "$.kind" (str "$" o "kind") with
                    | AgainstMutant -> RedEvidence.CreateAgainstMutant(outcomes, 1)
                    | kind -> RedEvidence.Create(outcomes, kind)
                match created with
                | Ok red -> TestsAuthored red
                | Error e -> fail $"$.outcomes: {e}"
            | "returned_to_test_author" -> ReturnedToTestAuthor(str "$" o "problem")
            | "review_completed" ->
                ReviewCompleted
                    { Blocking = decodeFindings o "blocking"
                      Advisory = decodeFindings o "advisory"
                      Independence = independenceOf "$.independence" (str "$" o "independence") }
            | "review_accepted" -> ReviewAccepted(str "$" o "note", independenceOf "$.independence" (str "$" o "independence"))
            | "review_guided" -> ReviewGuided(str "$" o "guidance")
            | "abandon" -> Abandon(str "$" o "reason")
            | other -> fail $"$.type: unknown task event '{other}'")
