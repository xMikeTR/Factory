namespace Factory.Core

open System

/// What the router needs from one run-ledger row. The Harness maps rows into these; Core never parses JSON.
type RouteFact = { Task: string; Role: Role; Repo: string option; Size: SizeBucket }

type TurnFact = { Task: string; Role: Role; Model: string; TokensIn: int; TokensOut: int; Ts: DateTime }

type VerifyFact = { Task: string; Passed: bool; Ts: DateTime }

type ReviewFact = { Task: string; Model: string; Approved: bool; Ts: DateTime }

type LearnEvent =
    /// A `route` row: gives the role, size and repo a task's later turns are keyed by.
    | Routed of RouteFact
    /// An act turn (or any turn; only the test author's and implementer's are credited).
    | Turn of TurnFact
    /// One verify result of a task: the unit of evidence.
    | Verified of VerifyFact
    /// The reviewer's verdict on the task's diff.
    | Reviewed of ReviewFact

/// One piece of evidence: who, whether it passed, tokens spent, when.
type Attempt = StatsKey * bool * int * int * DateTime

/// The ledger is the source of truth: stats are always a fold of the attempts it contains.
[<RequireQualifiedAccess>]
module Learning =
    let private isAct role = (role = Role.Implementer || role = Role.TestAuthor)

    /// D3: escalation is per turn. Each verify result credits (or debits) the model of the last act turn before it,
    /// with that model's tokens since the previous verify. A verdict from the reviewer counts for the reviewer's role
    /// key only when `learnReviewer` is on (weak evidence). Models the catalog doesn't know, and tasks with no
    /// route row for the role, give no evidence. Events must be in ledger order.
    let attribute (known: string -> bool) (learnReviewer: bool) (events: LearnEvent list) : Attempt list =
        let routes =
            events
            |> List.choose (function Routed r -> Some((r.Task, r.Role), r) | _ -> None)
            |> Map.ofList
        let keyFor task role model =
            if not (known model) then None
            else
                routes
                |> Map.tryFind (task, role)
                |> Option.map (fun r -> { Model = model; Role = role; Repo = r.Repo; Size = r.Size })
        let step (window: Map<string, TurnFact list>, out: Attempt list) event =
            match event with
            | Routed _ -> window, out
            | Turn t when isAct t.Role ->
                let w = Map.tryFind t.Task window |> Option.defaultValue []
                Map.add t.Task (t :: w) window, out
            | Turn _ -> window, out
            | Verified v ->
                let w = Map.tryFind v.Task window |> Option.defaultValue []
                let window' = Map.remove v.Task window
                match w with
                | [] -> window', out
                | last :: _ ->
                    match keyFor v.Task last.Role last.Model with
                    | None -> window', out
                    | Some key ->
                        let mine = w |> List.filter (fun t -> t.Model = last.Model && t.Role = last.Role)
                        let tin = mine |> List.sumBy (fun t -> t.TokensIn)
                        let tout = mine |> List.sumBy (fun t -> t.TokensOut)
                        window', (key, v.Passed, tin, tout, v.Ts) :: out
            | Reviewed r when learnReviewer ->
                match keyFor r.Task Role.Reviewer r.Model with
                | Some key -> window, (key, r.Approved, 0, 0, r.Ts) :: out
                | None -> window, out
            | Reviewed _ -> window, out
        events |> List.fold step (Map.empty, []) |> snd |> List.rev

    /// Weight of evidence from `ts`: 0.5 ** (ageDays / halfLifeDays). Continuous, so it replaces "halve monthly".
    /// An event from the future counts in full.
    let weight (now: DateTime) (halfLifeDays: float) (ts: DateTime) : float =
        let ageDays = max 0.0 (now - ts).TotalDays
        Math.Pow(0.5, ageDays / halfLifeDays)

    /// Folds attempts into stats: each success or failure counts for its weight; token means and samples are unweighted.
    /// Catalog priors are not folded in here: `Router` adds them when it draws.
    let fold (now: DateTime) (halfLifeDays: float) (start: Stats) (attempts: Attempt list) : Stats =
        attempts
        |> List.fold
            (fun stats (key, passed, tin, tout, ts) ->
                let w = weight now halfLifeDays ts
                let before = Map.tryFind key stats |> Option.map (fun c -> c.Successes, c.Failures) |> Option.defaultValue (0.0, 0.0)
                let recorded = Stats.record key passed tin tout stats
                let c = recorded.[key]
                let s0, f0 = before
                Map.add key { c with Successes = (if passed then s0 + w else s0); Failures = (if passed then f0 else f0 + w) } recorded)
            start
