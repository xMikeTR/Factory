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

    /// npm's placeholder from `npm init`: no tests, and it always fails.
    let private placeholder (script: string) = script.Contains "no test specified"

    let private hasTestScript (packageJson: string) =
        try
            match System.Text.Json.Nodes.JsonNode.Parse packageJson with
            | :? System.Text.Json.Nodes.JsonObject as o ->
                match o["scripts"] with
                | :? System.Text.Json.Nodes.JsonObject as scripts ->
                    match scripts["test"] with
                    | null -> false
                    | t -> t.GetValueKind() = System.Text.Json.JsonValueKind.String && not (placeholder (t.GetValue<string>()))
                | _ -> false
            | _ -> false
        with _ -> false

    let private directory (path: string) = match path.LastIndexOf '/' with -1 -> "" | i -> path.Substring(0, i)

    /// `solutions` are the repo's .sln/.slnx paths, `projects` its .csproj/.fsproj paths and `packages` its
    /// package.json paths, each with their text, '/'-separated and relative to the repo.
    /// .NET: one solution or project at the root keeps `dotnet test` there; otherwise every test project runs
    /// (MSB1003: several projects and no solution at the root, run 20261016-095826).
    /// npm: a root package with a test script runs alone (a workspace root's script covers its packages);
    /// otherwise `npm test` in each package folder that has one. Every step runs even when one before it fails.
    let discover (solutions: string list) (projects: (string * string) list) (packages: (string * string) list) : SuitePlan =
        let projects = projects |> List.filter (fst >> inRepo)
        let rootEntries = (solutions |> List.filter atRoot) @ (projects |> List.map fst |> List.filter atRoot)
        let dotnet =
            if rootEntries.Length = 1 then Some "dotnet test || s=1"
            else
                match projects |> List.filter (snd >> isTestProject) |> List.map fst |> List.sort with
                | [] -> None
                | tests ->
                    let words = tests |> List.map quote |> String.concat " "
                    Some $"for p in {words}; do dotnet test \"$p\" || s=1; done"
        let npm =
            let withTests = packages |> List.filter (fun (p, text) -> inRepo p && hasTestScript text) |> List.map (fst >> directory) |> List.sort
            if List.contains "" withTests then [ "npm test || s=1" ]
            else withTests |> List.map (fun d -> $"(cd {quote d} && npm test) || s=1")
        match dotnet, npm with
        | Some "dotnet test || s=1", [] -> SuitePlan.Command "dotnet test"
        | None, [] -> SuitePlan.NoSuite
        | _ -> SuitePlan.Command("s=0; " + String.concat "; " (Option.toList dotnet @ npm) + "; exit $s")
