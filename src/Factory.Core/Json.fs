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

    // ---------- handover ----------

    let private decodeElement path n =
        let o = obj path n
        { Id = ElementId(str path o "id")
          Name = str path o "name"
          CodeAreas = strs path o "codeAreas" |> List.map (pattern $"{path}.codeAreas")
          Risk = risk $"{path}.risk" (str path o "risk")
          InScope = inScope o }

    let private decodeDecision path n =
        let o = obj path n
        { Id = DecisionId(str path o "id")
          Statement = str path o "statement"
          Binds = strs path o "binds" |> List.map ElementId
          Checks =
            arr path o "checks"
            |> List.map (fun (p, c) ->
                let co = obj p c
                { Kind = str p co "kind"; Run = str p co "run" }) }

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
                    { Path = str p so "path"; Hash = str p so "hash" }) })

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
                   "inScope", JsonValue.Create e.InScope ]
        let decision (d: BoundDecision) =
            node [ "id", JsonValue.Create d.Id.Value
                   "statement", JsonValue.Create d.Statement
                   "binds", strArr (d.Binds |> List.map (fun b -> b.Value))
                   "checks", JsonArray(d.Checks |> List.map (fun c -> node [ "kind", JsonValue.Create c.Kind; "run", JsonValue.Create c.Run ]) |> Array.ofList) ]
        let root =
            node [ "id", JsonValue.Create h.Id
                   "digest", JsonValue.Create h.Digest
                   "elements", JsonArray(h.Elements |> List.map element |> Array.ofList)
                   "decisions", JsonArray(h.Decisions |> List.map decision |> Array.ofList)
                   "protectedPaths", strArr (h.ProtectedPaths |> List.map (fun p -> p.Value))
                   "eventVocabulary", strArr h.EventVocabulary
                   "relationships", JsonArray(h.Relationships |> List.map (fun r -> node [ "from", JsonValue.Create r.From.Value; "to", JsonValue.Create r.To.Value ]) |> Array.ofList)
                   "acceptanceSpecs", JsonArray(h.AcceptanceSpecs |> List.map (fun a -> node [ "path", JsonValue.Create a.Path; "hash", JsonValue.Create a.Hash ]) |> Array.ofList) ]
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
