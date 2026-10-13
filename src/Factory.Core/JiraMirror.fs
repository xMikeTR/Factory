namespace Factory.Core

open System.Text.RegularExpressions

/// What one mirrored Jira issue needs, judged from the ledger's state of the work item.
type MirrorAction =
    /// Jira is behind the ledger (it still shows what the mirror last wrote, or a fresh "To Do"): move it.
    | Transition of key: string * item: string * from: string * target: string
    | InSync of key: string
    /// Someone changed the issue in Jira, so it matches neither the ledger nor what the mirror wrote.
    /// Never overwritten: a person decides which side is right.
    | Drift of key: string * item: string * jira: string * wanted: string
    | NotInJira of key: string * item: string

/// One-way mirror, ledger -> Jira (pipeline-contract: "the ledger is the queue's source of truth; Jira is a one-way
/// mirror"). Pure: it plans transitions from the board's work states and a snapshot of the issues' statuses.
[<RequireQualifiedAccess>]
module JiraMirror =
    let private jiraKey = Regex(@"^[A-Z][A-Z0-9]+-\d+$", RegexOptions.Compiled)

    /// The issue an item mirrors to: a `jira` target's key, or a repo item whose requirement is a Jira key.
    let key (item: WorkItem) : string option =
        match item.Target with
        | Target.Jira k -> Some k
        | Target.Repo(req, _, _) when jiraKey.IsMatch req -> Some req
        | _ -> None

    /// The Jira status a work state implies. Only three: the mirror does not invent workflow steps.
    let statusOf (state: WorkState) : string =
        match state with
        | WorkState.Done _ -> "Done"
        | WorkState.Active _
        | WorkState.AwaitingHuman _
        | WorkState.Blocked _ -> "In Progress"
        | WorkState.Backlog
        | WorkState.Ready _ -> "To Do"

    /// `jira`: the issues' current statuses. `mirrored`: the status the mirror last set per key.
    let plan (items: (WorkItem * WorkState) list) (jira: Map<string, string>) (mirrored: Map<string, string>) : MirrorAction list =
        items
        |> List.choose (fun (item, state) ->
            key item
            |> Option.map (fun k ->
                let wanted = statusOf state
                match Map.tryFind k jira with
                | None -> NotInJira(k, item.Id.Value)
                | Some current when current = wanted -> InSync k
                | Some current ->
                    let wasMirrored = Map.tryFind k mirrored = Some current
                    // A new issue sits at "To Do" before the mirror has ever written it; anything else is a person's edit.
                    if wasMirrored || (Map.tryFind k mirrored).IsNone && current = "To Do" then
                        Transition(k, item.Id.Value, current, wanted)
                    else
                        Drift(k, item.Id.Value, current, wanted)))
