namespace Factory.Core

/// Where a file in a turn's context pack came from. Defaults and the task's own written files are
/// deterministic; nominations are the task-planner's, validated here and never trusted.
type ContextSource =
    | Default
    | Nominated of reason: string
    | TaskWritten

type ContextEntry = { Path: string; Source: ContextSource }

type ContextLimits = { MaxNominations: int; MaxChars: int }

type Nomination = { Path: string; Reason: string }

/// The frozen selection for one task: chosen once, at the approach turn. Rejections are reported
/// back to the planner; they never fail the turn.
type Selection = { Entries: ContextEntry list; Rejected: (string * string) list }

module ContextPack =

    let defaultLimits = { MaxNominations = 12; MaxChars = 60_000 }

    let private isProject (f: string) = f.EndsWith ".csproj" || f.EndsWith ".fsproj"

    let private segs (path: string) = path.Split('/', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    let private isPrefixOf (a: string list) (b: string list) = a.Length <= b.Length && List.take a.Length b = a

    /// Existing files every acting turn gets: the project file owning each write root, one existing
    /// test file under testWrites as the convention example, and existing files matching writes.
    let defaults (repoFiles: string list) (writes: PathPattern list) (testWrites: PathPattern list) : string list =
        let files = repoFiles |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))
        let projectsFor roots =
            files
            |> List.filter isProject
            |> List.filter (fun f ->
                let dir = segs f |> List.rev |> List.tail |> List.rev
                roots |> List.exists (fun r -> isPrefixOf dir r || isPrefixOf r dir))
        let roots = (writes @ testWrites) |> List.map PathPattern.literalPrefix
        let written = files |> List.filter (fun f -> not (isProject f) && writes |> List.exists (fun w -> PathPattern.matches w f))
        let convention =
            files |> List.tryFind (fun f -> not (isProject f) && testWrites |> List.exists (fun w -> PathPattern.matches w f))
        (projectsFor roots @ written @ Option.toList convention) |> List.distinct

    let private reject (limits: ContextLimits) (exists: string -> bool) (taken: Set<string>) (accepted: int) (n: Nomination) =
        if System.String.IsNullOrWhiteSpace n.Reason then Error "a nomination needs a reason"
        else
            match PathPattern.create n.Path with
            | Error e -> Error e
            | Ok pat when not (PathPattern.isExact pat) -> Error "a nomination names one file, not a pattern"
            | Ok pat ->
                let path = pat.Raw
                if path.StartsWith ".git/" || path.StartsWith ".factory/" then Error "not readable context"
                elif Set.contains path taken then Error "already in the pack"
                elif not (exists path) then Error "does not exist"
                elif accepted >= limits.MaxNominations then Error $"over the limit of {limits.MaxNominations} nominations"
                else Ok path

    /// Validates nominations against the repo. Defaults come first and are never displaced.
    let select (limits: ContextLimits) (exists: string -> bool) (defaults: string list) (nominations: Nomination list) : Selection =
        let start = defaults |> List.distinct
        let entries, rejected, _, _ =
            nominations
            |> List.fold (fun (entries, rejected, taken, accepted) n ->
                match reject limits exists taken accepted n with
                | Ok path -> entries @ [ { Path = path; Source = Nominated n.Reason } ], rejected, Set.add path taken, accepted + 1
                | Error why -> entries, rejected @ [ n.Path, why ], taken, accepted)
                (start |> List.map (fun p -> { Path = p; Source = Default }), [], Set.ofList start, 0)
        { Entries = entries; Rejected = rejected }
