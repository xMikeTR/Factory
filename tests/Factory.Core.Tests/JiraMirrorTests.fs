module Factory.Core.Tests.JiraMirrorTests

open Factory.Core
open Factory.Core.Tests.Support

let private jiraItem id key =
    match
        WorkItem.create
            { Id = id; Requirement = None; Elements = []; Repo = None; Target = Some("jira", key)
              DependsOn = []; Writes = []; DoneWhen = [ "checklist", "comment posted" ] }
    with
    | Ok i -> i
    | Error es -> failwithf "%A" es

let private repoItem id req =
    match
        WorkItem.create
            { Id = id; Requirement = Some req; Elements = [ id ]; Repo = None; Target = None
              DependsOn = []; Writes = []; DoneWhen = [ "gate", "dotnet test" ] }
    with
    | Ok i -> i
    | Error es -> failwithf "%A" es

let private active = WorkState.Active(RunId "r1")

let tests: (string * (unit -> unit)) list =
    [ "jira mirror: a jira target and a repo item with a jira-style requirement mirror; others do not", fun () ->
          equal (Some "HRE-7") (JiraMirror.key (jiraItem "W-A" "HRE-7")) "target"
          equal (Some "HRE-9") (JiraMirror.key (repoItem "W-B" "HRE-9")) "requirement"
          equal None (JiraMirror.key (repoItem "W-C" "pricing-rules")) "not a key"
      "jira mirror: Jira behind the ledger is moved when it shows what the mirror wrote", fun () ->
          let plan = JiraMirror.plan [ jiraItem "W-A" "HRE-7", active ] (Map [ "HRE-7", "To Do" ]) (Map [ "HRE-7", "To Do" ])
          equal [ Transition("HRE-7", "W-A", "To Do", "In Progress") ] plan "transition"
      "jira mirror: a status nobody mirrored is drift and is never overwritten", fun () ->
          let plan = JiraMirror.plan [ jiraItem "W-A" "HRE-7", WorkState.Backlog ] (Map [ "HRE-7", "Done" ]) (Map [ "HRE-7", "To Do" ])
          equal [ Drift("HRE-7", "W-A", "Done", "To Do") ] plan "drift"
      "jira mirror: a never-mirrored issue at To Do is a new issue to move, anything else is drift", fun () ->
          let a = jiraItem "W-A" "HRE-7"
          equal [ Transition("HRE-7", "W-A", "To Do", "In Progress") ] (JiraMirror.plan [ a, active ] (Map [ "HRE-7", "To Do" ]) Map.empty) "new"
          equal [ Drift("HRE-7", "W-A", "In Progress", "To Do") ] (JiraMirror.plan [ a, WorkState.Backlog ] (Map [ "HRE-7", "In Progress" ]) Map.empty) "edited"
      "jira mirror: matching and missing issues are reported as such", fun () ->
          let a, b = jiraItem "W-A" "HRE-7", jiraItem "W-B" "HRE-8"
          equal [ InSync "HRE-7"; NotInJira("HRE-8", "W-B") ] (JiraMirror.plan [ a, WorkState.Backlog; b, WorkState.Backlog ] (Map [ "HRE-7", "To Do" ]) Map.empty) "sync" ]
