namespace Factory.Core

open System

/// A request tracked as one item (designc's `work/<id>.yaml`; the file format is Designc's, read
/// by Harness/Registry/WorkRegistryReader). All work, repo or not, is a WorkItem; the target decides
/// what done means. Nothing here does I/O.
type WorkItemId =
    | WorkItemId of string
    member this.Value = match this with WorkItemId v -> v

type RunId =
    | RunId of string
    member this.Value = match this with RunId v -> v

type NonEmptyList<'a> =
    { Head: 'a
      Tail: 'a list }
    member this.ToList = this.Head :: this.Tail

type Target =
    /// Code, via designc -> S2: the requirement, the design elements it changes, the repo (if the registry has several).
    | Repo of requirement: string * elements: ElementId list * repo: string option
    | Document of string
    | Jira of string
    | Investigation of string

/// How one entry of done_when is satisfied. A Judge is a gradient: never required, never alone.
/// Parity names a scenario id under the registry's parity/scenarios, or "all"; it is met only by a recorded
/// parity_result, so it is not a gate and a repo item still needs one.
type Check =
    | Gate of string
    | Checklist of string
    | Review of string
    | Judge of string
    | Parity of string

[<RequireQualifiedAccess>]
module Check =
    /// The key a person's tick is stored under (designc's `entryKey`).
    let key =
        function
        | Gate v -> $"gate:{v}"
        | Checklist v -> $"checklist:{v}"
        | Review v -> $"review:{v}"
        | Judge v -> $"judge:{v}"
        | Parity v -> $"parity:{v}"

type WorkItem =
    { Id: WorkItemId
      Target: Target
      DependsOn: WorkItemId list
      /// Repo items only; empty otherwise.
      Writes: PathPattern list
      DoneWhen: NonEmptyList<Check> }

/// What a work item file says, before any rule is applied.
type WorkDraft =
    { Id: string
      Requirement: string option
      Elements: string list
      Repo: string option
      /// ("document" | "jira" | "investigation", value)
      Target: (string * string) option
      DependsOn: string list
      Writes: string list
      /// ("gate" | "checklist" | "review" | "judge" | "parity", value)
      DoneWhen: (string * string) list }

type WorkError =
    | NoId
    | NeedsRequirementOrTarget
    | UnknownTargetKind of string
    | BlankTarget
    | RepoWithoutElements
    | WriteNotAPathPattern of string * reason: string
    | EmptyDoneWhen
    | UnknownCheckKind of entry: int * kind: string
    | BlankCheck of entry: int * kind: string
    | JudgeOnly
    | RepoWithoutGate
    | ParityWithoutRepo

[<RequireQualifiedAccess>]
module WorkError =
    let describe id =
        function
        | NoId -> "The work item has no id."
        | NeedsRequirementOrTarget -> $"{id}: name either a requirement (with elements) or a target, not both and not neither."
        | UnknownTargetKind k -> $"{id}: target kind '{k}' is not document, jira or investigation."
        | BlankTarget -> $"{id}: target needs a value."
        | RepoWithoutElements -> $"{id}: a repo item needs elements, the design elements it changes."
        | WriteNotAPathPattern(w, r) -> $"{id}: writes entry '{w}' is not a path pattern: {r}"
        | EmptyDoneWhen -> $"{id}: done_when is empty; say what done means with at least one gate, checklist or review entry."
        | UnknownCheckKind(i, k) -> $"{id}: done_when entry {i} has unknown kind {k}; use gate, checklist, review, judge or parity."
        | BlankCheck(i, k) -> $"{id}: done_when entry {i} ({k}) is empty."
        | JudgeOnly -> $"{id}: a judge cannot be the only done_when entry; add a gate, checklist or review (a judge is a gradient, not a decision)."
        | RepoWithoutGate -> $"{id}: a repo item needs a gate in done_when (a command that passes at the commit)."
        | ParityWithoutRepo -> $"{id}: parity compares a commit of the new app with legacy, so only a repo item can name it in done_when."

[<RequireQualifiedAccess>]
module WorkItem =
    /// The only door into WorkItem: every rule of designc's Work.validate that does not need the
    /// registry (requirement and dependency existence are checked where the registry is read).
    /// All errors are collected.
    let create (d: WorkDraft) : Result<WorkItem, WorkError list> =
        let blank (s: string) = String.IsNullOrWhiteSpace s
        let idErr = if blank d.Id then [ NoId ] else []
        let hasReq = d.Requirement |> Option.exists (blank >> not)

        let target, targetErr =
            match hasReq, d.Target with
            | true, None ->
                (if d.Elements.IsEmpty then None else Some(Repo(d.Requirement.Value, d.Elements |> List.map ElementId, d.Repo))),
                (if d.Elements.IsEmpty then [ RepoWithoutElements ] else [])
            | false, Some(kind, v) ->
                let make =
                    match kind with
                    | "document" -> Ok Document
                    | "jira" -> Ok Jira
                    | "investigation" -> Ok Investigation
                    | k -> Error(UnknownTargetKind k)
                match make with
                | Error e -> None, [ e ]
                | Ok _ when blank v -> None, [ BlankTarget ]
                | Ok f -> Some(f (v.Trim())), []
            | _ -> None, [ NeedsRequirementOrTarget ]

        let writes, writeErr =
            d.Writes
            |> List.map (fun w -> PathPattern.create w |> Result.mapError (fun r -> WriteNotAPathPattern(w, r)))
            |> List.fold
                (fun (ok, bad) r -> match r with Ok p -> ok @ [ p ], bad | Error e -> ok, bad @ [ e ])
                ([], [])

        let checks, checkErr =
            d.DoneWhen
            |> List.mapi (fun i (kind, v) ->
                let n = i + 1
                match kind with
                | _ when not (List.contains kind [ "gate"; "checklist"; "review"; "judge"; "parity" ]) -> Error(UnknownCheckKind(n, kind))
                | _ when blank v -> Error(BlankCheck(n, kind))
                | "gate" -> Ok(Gate(v.Trim()))
                | "checklist" -> Ok(Checklist(v.Trim()))
                | "review" -> Ok(Review(v.Trim()))
                | "parity" -> Ok(Parity(v.Trim()))
                | _ -> Ok(Judge(v.Trim())))
            |> List.fold
                (fun (ok, bad) r -> match r with Ok c -> ok @ [ c ], bad | Error e -> ok, bad @ [ e ])
                ([], [])

        let isJudge = function Judge _ -> true | _ -> false
        let isGate = function Gate _ -> true | _ -> false
        let isParity = function Parity _ -> true | _ -> false
        let doneErr =
            if d.DoneWhen.IsEmpty then [ EmptyDoneWhen ]
            elif checkErr.IsEmpty && checks |> List.forall isJudge then [ JudgeOnly ]
            elif checkErr.IsEmpty && hasReq && not (List.exists isGate checks) then [ RepoWithoutGate ]
            else []
        let parityErr = if not hasReq && List.exists isParity checks then [ ParityWithoutRepo ] else []

        match idErr @ targetErr @ writeErr @ checkErr @ doneErr @ parityErr, target, checks with
        | [], Some t, c :: rest ->
            Ok { Id = WorkItemId(d.Id.Trim())
                 Target = t
                 DependsOn = d.DependsOn |> List.map WorkItemId
                 Writes = writes
                 DoneWhen = { Head = c; Tail = rest } }
        | errors, _, _ -> Error errors

    let isRepo (i: WorkItem) = match i.Target with Repo _ -> true | _ -> false

    /// The design elements a repo item changes (empty for other targets).
    let elements (i: WorkItem) = match i.Target with Repo(_, es, _) -> es | _ -> []

/// One recorded run of a gate against a work item (designc's `item_gate`).
type GateRun =
    { Gate: string
      Commit: string option
      Passed: bool
      Dirty: bool }

type ParityOutcome =
    | ParityPass
    | ParityFail
    | ParityCouldNotRun

[<RequireQualifiedAccess>]
module ParityOutcome =
    /// designc's `parity_result.outcome` vocabulary.
    let toLedger =
        function
        | ParityPass -> "pass"
        | ParityFail -> "fail"
        | ParityCouldNotRun -> "could_not_run"

    let parse =
        function
        | "pass" -> Some ParityPass
        | "fail" -> Some ParityFail
        | "could_not_run" -> Some ParityCouldNotRun
        | _ -> None

/// One recorded parity_result: a scenario replayed on the new app at the commit the recorder ran against.
type ParityRun =
    { Scenario: string
      Commit: string
      Outcome: ParityOutcome }

/// What is known when someone says a work item is done.
type EvidenceFacts =
    { Commit: string option
      /// Some true / Some false from git; None when it could not be checked.
      OnMain: bool option
      /// In ledger order, oldest first.
      GateRuns: GateRun list
      /// Check.key values a person ticked.
      Ticks: string list
      /// In ledger order, oldest first, for any item: a result is about a scenario at a commit.
      ParityRuns: ParityRun list
      /// The registry's scenario ids (parity/scenarios), what "parity: all" means.
      ParityScenarios: string list }

/// Proof that every check of a work item is met. Only `Evidence.create` makes one, so a Done state
/// that exists is a Done that was judged from recorded evidence, never from a claim.
type Evidence =
    private
    | Evidence of commit: string option * covered: string list

    member this.Commit = match this with Evidence(c, _) -> c
    member this.Covered = match this with Evidence(_, k) -> k

[<RequireQualifiedAccess>]
module Evidence =
    /// Ok when every Gate has a clean passing last run (at `Commit`, for a repo item), every
    /// Checklist and Review is ticked, every Parity scenario's last result at `Commit` is a pass, and
    /// (repo items) the commit is on main. A Judge needs nothing.
    let create (item: WorkItem) (f: EvidenceFacts) : Result<Evidence, string list> =
        let repo = WorkItem.isRepo item
        let short (c: string) = if c.Length > 7 then c.Substring(0, 7) else c
        let commitProblems =
            if not repo then []
            else
                match f.Commit, f.OnMain with
                | None, _ -> [ "No commit given: say which commit finished this item." ]
                | Some c, Some false -> [ $"Commit {short c} is not on main. Merge it first; if it is merged, fetch in the repo and try again." ]
                | Some c, None -> [ $"Could not check that commit {short c} is on main." ]
                | Some _, Some true -> []
        let noPassingRun g =
            match f.Commit with
            | Some c when repo -> $"{g} has no passing run at {short c}."
            | _ -> $"{g} has no passing run."
        let problems =
            item.DoneWhen.ToList
            |> List.collect (function
                | Gate g ->
                    let last = f.GateRuns |> List.filter (fun r -> r.Gate = g && (not repo || r.Commit = f.Commit)) |> List.tryLast
                    match last with
                    | Some r when r.Passed && not r.Dirty -> []
                    | _ -> [ noPassingRun g ]
                | (Checklist _ | Review _) as c -> if List.contains (Check.key c) f.Ticks then [] else [ $"{Check.key c} is not ticked." ]
                | Parity "all" when f.ParityScenarios.IsEmpty -> [ "parity: all, but the registry has no parity scenarios." ]
                | Parity p ->
                    (if p = "all" then f.ParityScenarios else [ p ])
                    |> List.collect (fun s ->
                        let last = f.ParityRuns |> List.filter (fun r -> r.Scenario = s && Some r.Commit = f.Commit) |> List.tryLast
                        match last, f.Commit with
                        | Some r, _ when r.Outcome = ParityPass -> []
                        | Some r, Some c -> [ $"parity {s} did not pass at {short c} ({ParityOutcome.toLedger r.Outcome})." ]
                        | _, Some c -> [ $"parity {s} has no result at {short c}." ]
                        | _, None -> [ $"parity {s} has no result." ])
                | Judge _ -> [])
        match commitProblems @ problems with
        | [] -> Ok(Evidence(f.Commit, item.DoneWhen.ToList |> List.filter (function Judge _ -> false | _ -> true) |> List.map Check.key))
        | es -> Error es

/// Why an item is frozen: the handover it became and the design version (registry revision) it pinned.
type FrozenAt = { Handover: string; DesignVersion: string }

[<RequireQualifiedAccess>]
type WorkState =
    | Backlog
    | Ready of FrozenAt
    | Active of RunId
    | AwaitingHuman of RunId * reason: string
    | Done of Evidence
    | Blocked of reason: string

[<RequireQualifiedAccess>]
type WorkEvent =
    | Frozen of FrozenAt
    | Pulled of RunId
    | AwaitedHuman of reason: string
    | Resumed
    | Finished of Evidence
    | Rechecked of reason: string
    | BlockedBy of reason: string
    | Unblocked

[<RequireQualifiedAccess>]
module WorkState =
    /// Backlog --Frozen--> Ready --Pulled--> Active <--Resumed-- AwaitingHuman <--AwaitedHuman-- Active
    /// Active --Finished--> Done. A recheck sends Backlog or Ready back to Backlog. Done is final.
    let apply (s: WorkState) (e: WorkEvent) : Result<WorkState, string> =
        match s, e with
        | WorkState.Backlog, WorkEvent.Frozen f -> Ok(WorkState.Ready f)
        | WorkState.Ready _, WorkEvent.Pulled r -> Ok(WorkState.Active r)
        | WorkState.Active r, WorkEvent.AwaitedHuman why -> Ok(WorkState.AwaitingHuman(r, why))
        | WorkState.AwaitingHuman(r, _), WorkEvent.Resumed -> Ok(WorkState.Active r)
        | WorkState.Active _, WorkEvent.Finished ev -> Ok(WorkState.Done ev)
        | (WorkState.Backlog | WorkState.Ready _), WorkEvent.Rechecked _ -> Ok WorkState.Backlog
        | (WorkState.Backlog | WorkState.Ready _ | WorkState.Active _ | WorkState.AwaitingHuman _), WorkEvent.BlockedBy why -> Ok(WorkState.Blocked why)
        | WorkState.Blocked _, WorkEvent.Unblocked -> Ok WorkState.Backlog
        | s, e -> Error $"{e} is not allowed from {s}"

    let runOf =
        function
        | WorkState.Active r
        | WorkState.AwaitingHuman(r, _) -> Some r
        | _ -> None

    /// Counts toward the WIP limit. AwaitingHuman does, deliberately: a pile of runs waiting on a
    /// person is the bottleneck and must block new pulls.
    let inFlight s = (runOf s).IsSome
