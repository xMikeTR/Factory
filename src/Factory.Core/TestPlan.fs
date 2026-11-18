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

/// Which tests a task's change can affect, beyond the ones designc mapped. Pure: the Factory runs the tools and hands the text in.
/// Tools and flags as checked on 17 Nov 2026: dotnet-affected 7.0.1 (`--from`, `--format json`, `--dry-run`; exit 166 = nothing affected)
/// and Vitest 5.1.0 (`vitest related --run`, `--passWithNoTests`).
[<RequireQualifiedAccess>]
module Affected =

    let private quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"

    /// The dotnet-affected call for the projects that own the changed files (`--assume-changes`, one flag per project, repo-relative paths).
    /// Not `--from <branch>`: that opens the git repository, and in the Docker sandbox only the worktree is mounted, so its `.git` file points
    /// at a folder the container cannot see (checked on 7.0.1: RepositoryNotFoundException). The Factory already knows the changed files.
    let dotnetCommand (projects: string list) =
        "dotnet-affected " + String.concat " " (projects |> List.map (fun p -> $"--assume-changes {quote p}")) + " --format json --dry-run"

    let private isProjectFile (p: string) = p.EndsWith(".csproj", StringComparison.Ordinal) || p.EndsWith(".fsproj", StringComparison.Ordinal)

    /// The projects (among the repo's `projects`) that own the changed files: the nearest project file in the file's own folder or above.
    /// A changed project file owns itself. A file under no project gives none.
    let owningProjects (projects: string list) (changed: string list) : string list =
        let dirOfP (p: string) = match p.LastIndexOf '/' with -1 -> "" | i -> p.Substring(0, i)
        let ownerOf (f: string) =
            if isProjectFile f then (if List.contains f projects then Some f else None)
            else
                projects
                |> List.filter (fun p -> let d = dirOfP p in d = "" || f.StartsWith(d + "/", StringComparison.Ordinal))
                |> function [] -> None | ps -> Some(List.maxBy (fun p -> (dirOfP p).Length) ps)
        changed |> List.choose ownerOf |> List.distinct |> List.sort

    let dotnetNoneExit = 166

    /// dotnet-affected's `--dry-run` prints two `DRY-RUN:` lines before the JSON array of `{Name, FilePath}` (recorded from 7.0.1, see the test),
    /// FilePath absolute. Ok [] for exit 166; Error for any other failure or output that is not that array.
    let parseDotnet (exit: int) (stdout: string) : Result<string list, string> =
        if exit = dotnetNoneExit then Ok []
        elif exit <> 0 then Error $"dotnet-affected exited {exit}"
        else
            let start = stdout.IndexOf '['
            if start < 0 then Error "dotnet-affected printed no JSON array"
            else
                try
                    use doc = System.Text.Json.JsonDocument.Parse(stdout.Substring start)
                    doc.RootElement.EnumerateArray()
                    |> Seq.map (fun p -> p.GetProperty("FilePath").GetString())
                    |> List.ofSeq
                    |> Ok
                with e -> Error $"dotnet-affected output is not the expected JSON: {e.Message}"

    /// `path` under `root`, '/'-separated; None when it is elsewhere.
    let relativeTo (root: string) (path: string) : string option =
        let r = root.Replace('\\', '/').TrimEnd '/' + "/"
        let p = path.Replace('\\', '/')
        if p.StartsWith(r, StringComparison.Ordinal) then Some(p.Substring r.Length) else None

    /// The affected test projects as `dotnet test` commands: only projects whose text says they are test projects, in path order.
    let dotnetTests (affected: (string * string) list) : (string * string) list =
        affected
        |> List.filter (fun (_, text) -> Suite.isTestProject text)
        |> List.map fst
        |> List.sort
        |> List.map (fun p -> p, $"dotnet test {quote p}")

    let private sourceExt = [ ".ts"; ".tsx"; ".js"; ".jsx"; ".mts"; ".cts"; ".mjs"; ".cjs" ]

    let private dirOf (path: string) = match path.LastIndexOf '/' with -1 -> "" | i -> path.Substring(0, i)

    let private hasVitest (packageJson: string) =
        try
            match System.Text.Json.Nodes.JsonNode.Parse packageJson with
            | :? System.Text.Json.Nodes.JsonObject as o ->
                [ "dependencies"; "devDependencies" ]
                |> List.exists (fun k -> match o[k] with :? System.Text.Json.Nodes.JsonObject as d -> d.ContainsKey "vitest" | _ -> false)
            | _ -> false
        with _ -> false

    /// `vitest related` per package that has vitest: each changed script file goes to the nearest package.json above it, and only when that one
    /// lists vitest. `packages` are (package.json path, text). Result: (package dir, files relative to it, command), in dir order.
    let vitestRelated (packages: (string * string) list) (changed: string list) : (string * string list * string) list =
        let dirs = packages |> List.filter (fun (_, text) -> hasVitest text) |> List.map (fun (p, _) -> dirOf p)
        let allDirs = packages |> List.map (fun (p, _) -> dirOf p)
        let owner (f: string) =
            allDirs
            |> List.filter (fun d -> d = "" || f.StartsWith(d + "/", StringComparison.Ordinal))
            |> function [] -> None | ds -> Some(List.maxBy String.length ds)
        changed
        |> List.filter (fun f -> sourceExt |> List.exists (fun e -> f.EndsWith(e, StringComparison.Ordinal)))
        |> List.choose (fun f -> owner f |> Option.filter (fun d -> List.contains d dirs) |> Option.map (fun d -> d, (if d = "" then f else f.Substring(d.Length + 1))))
        |> List.groupBy fst
        |> List.sortBy fst
        |> List.map (fun (d, fs) ->
            let files = fs |> List.map snd |> List.distinct |> List.sort
            let call = "npx vitest related --run --passWithNoTests " + String.concat " " (files |> List.map quote)
            d, files, (if d = "" then call else $"cd {quote d} && {call}"))
