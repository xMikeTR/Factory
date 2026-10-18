namespace Factory.Core

/// The full suite of a repo that does not name one (`integration.testCommand`).
[<RequireQualifiedAccess>]
type SuitePlan =
    | Command of string
    /// No test project yet (a new repo): nothing to run, which is not a failure of the code.
    | NoSuite

[<RequireQualifiedAccess>]
module Suite =

    /// Build output, packages and the factory's own state: never the repo's projects.
    let ignoredDirectory (name: string) = Set.contains name (set [ "bin"; "obj"; "node_modules"; ".factory"; ".git" ])
    let private inRepo (path: string) = path.Split('/') |> Array.forall (ignoredDirectory >> not)
    let private atRoot (path: string) = not (path.Contains '/')
    let private isTestProject (content: string) =
        content.Contains "Microsoft.NET.Test.Sdk" || content.Contains "<IsTestProject>true</IsTestProject>"
    let private quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"

    /// `solutions` are the repo's .sln/.slnx paths and `projects` its .csproj/.fsproj paths with their text,
    /// '/'-separated and relative to the repo. One solution or project at the root keeps `dotnet test` there;
    /// otherwise every test project runs, each even when one before it fails (MSB1003: several projects and no
    /// solution at the root, run 20261016-095826).
    let discover (solutions: string list) (projects: (string * string) list) : SuitePlan =
        let projects = projects |> List.filter (fst >> inRepo)
        let rootEntries = (solutions |> List.filter atRoot) @ (projects |> List.map fst |> List.filter atRoot)
        if rootEntries.Length = 1 then SuitePlan.Command "dotnet test"
        else
            match projects |> List.filter (snd >> isTestProject) |> List.map fst |> List.sort with
            | [] -> SuitePlan.NoSuite
            | tests ->
                let words = tests |> List.map quote |> String.concat " "
                SuitePlan.Command($"s=0; for p in {words}; do dotnet test \"$p\" || s=1; done; exit $s")
