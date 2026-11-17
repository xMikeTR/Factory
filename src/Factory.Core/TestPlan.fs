namespace Factory.Core

open System

/// The tests one task is held to, taken from the handover's test map (designc `tests.json`).
type TaskTests =
    { FailToPass: NamedTest list
      PassToPass: NamedTest list
      /// Elements of the task with code and no existing test covering it, with designc's reason.
      Unmapped: (string * string) list }

[<RequireQualifiedAccess>]
module TestPlan =

    let private distinctByPath (tests: NamedTest list) = tests |> List.distinctBy (fun t -> t.Path)

    /// The repo a task works in, when the design is split: the repo its first repo-bound element names.
    let taskRepo (elements: ElementSpec list) (task: PlannedTask) : string option =
        elements |> List.filter (fun e -> List.contains e.Id task.Elements) |> List.tryPick (fun e -> e.Repo)

    /// The union over the task's elements, matched by element id (as `UiAcceptance.translationErrors` does), deduplicated by path.
    /// In a split project `repo` is the task's repo: a map entry for another repo's element is not this task's.
    let forTask (repo: string option) (map: TestMap) (task: PlannedTask) : TaskTests =
        let ids = task.Elements |> List.map (fun e -> e.Value) |> Set.ofList
        let mine =
            map.Elements
            |> List.filter (fun e -> ids.Contains e.Element && (e.Repo.IsNone || repo.IsNone || e.Repo = repo))
        { FailToPass = mine |> List.collect (fun e -> e.FailToPass) |> distinctByPath
          PassToPass = mine |> List.collect (fun e -> e.PassToPass) |> distinctByPath
          Unmapped = map.Unmapped |> List.filter (fun (e, _) -> ids.Contains e) }

    /// The tests the handover names for a task; None when it carries no test map.
    let ofHandover (h: Handover) (task: PlannedTask) : TaskTests option =
        h.Tests |> Option.map (fun m -> forTask (taskRepo h.Elements task) m task)

    /// The exact commands, each once, in the order the brief lists the tests.
    let commands (t: TaskTests) : string list =
        (t.FailToPass @ t.PassToPass) |> List.choose (fun n -> n.Command) |> List.distinct

    let isEmpty (t: TaskTests) = t.FailToPass.IsEmpty && t.PassToPass.IsEmpty && t.Unmapped.IsEmpty

    /// The brief's "## Tests for this task" section, or None when there is nothing to say.
    let briefSection (t: TaskTests) : string option =
        if isEmpty t then None
        else
            let lines = ResizeArray<string>()
            lines.Add "## Tests for this task"
            if not t.FailToPass.IsEmpty then
                lines.Add "Must pass at integration (frozen; do not edit):"
                for n in t.FailToPass do lines.Add $"- `{n.Path}`"
            if not t.PassToPass.IsEmpty then
                lines.Add "Already passing; keep them passing:"
                for n in t.PassToPass do lines.Add $"- `{n.Path}`"
            match commands t with
            | [] -> ()
            | cmds ->
                lines.Add "Run before declare_done:"
                for c in cmds do lines.Add c
            for (el, _) in t.Unmapped do
                lines.Add $"No existing test covers {el}; the tests you write are its only cover."
            Some(String.Join("\n", lines))

    /// Several commands as one shell line whose exit code is non-zero when any of them failed (the `Suite.discover` form).
    let shellLine (cmds: string list) : string =
        "s=0; " + String.concat "; " (cmds |> List.map (fun c -> c + " || s=1")) + "; exit $s"
