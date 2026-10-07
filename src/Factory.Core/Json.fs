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
          Risk = risk $"{path}.risk" (str path o "risk") }

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
              EventVocabulary = strs "$" o "eventVocabulary" })

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

    // ---------- task events (the ledger payload a runner replays on resume) ----------

    let private tierName = function Frontier -> "frontier" | Standard -> "standard" | Small -> "small"

    let private tierOf path = function
        | "frontier" -> Frontier
        | "standard" -> Standard
        | "small" -> Small
        | other -> fail $"{path}: unknown tier '{other}'"

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
        | DecisionBound d ->
            set "type" (JsonValue.Create "decision_bound")
            set "decision" (JsonValue.Create d.Value)
        | VerifyRequested -> set "type" (JsonValue.Create "verify_requested")
        | VerifyCompleted outcomes ->
            set "type" (JsonValue.Create "verify_completed")
            let items =
                outcomes
                |> List.map (fun v ->
                    let x = JsonObject()
                    x.["name"] <- JsonValue.Create v.Name
                    x.["exitCode"] <- JsonValue.Create v.ExitCode
                    x.["durationMs"] <- JsonValue.Create v.DurationMs
                    x.["outputTail"] <- JsonValue.Create v.OutputTail
                    x :> JsonNode)
                |> Array.ofList
            set "outcomes" (JsonArray(items))
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
                      Elements = strs "$" o "elements" |> List.map ElementId }
            | "decision_bound" -> DecisionBound(DecisionId(str "$" o "decision"))
            | "verify_requested" -> VerifyRequested
            | "verify_completed" ->
                VerifyCompleted(
                    arr "$" o "outcomes"
                    |> List.map (fun (p, n) ->
                        let x = obj p n
                        { Name = str p x "name"
                          ExitCode = int p x "exitCode" -1
                          DurationMs = (match x.["durationMs"] with null -> 0L | v -> v.GetValue<int64>())
                          OutputTail = defaultArg (optStr x "outputTail") "" })
                )
            | "abandon" -> Abandon(str "$" o "reason")
            | other -> fail $"$.type: unknown task event '{other}'")
