namespace Factory.Core

/// Deterministic code generation from the pinned OpenAPI contracts (R5a). Pure: it decides which files are generated
/// for which repo and which NSwag command makes each one; running the commands is `Factory.S2.Codegen`.
[<RequireQualifiedAccess>]
module Codegen =

    /// NuGet `NSwag.ConsoleCore`, command `nswag`. The runner image pins the same number (docker/runner/Dockerfile).
    let nswagVersion = "14.2.0"

    /// Where generated C# lives in a repo. Read-only to agents (S2Config.Default protects `generated/contracts/**`).
    let root = "generated/contracts/csharp"

    type Side =
        | Server
        | Client

    type Target =
        { Repo: string
          Source: string
          Stem: string
          Namespace: string
          Sides: Side list
          Files: string list }

    /// "contracts/openapi/fare-quotes.yaml" -> "FareQuotes": the file name without extension, PascalCase on `-`, `_` and `.`.
    let stem (path: string) : string =
        let file = path.Substring(path.LastIndexOf '/' + 1)
        let name = match file.LastIndexOf '.' with | -1 -> file | i -> file.Substring(0, i)
        name.Split([| '-'; '_'; '.' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun p -> string (System.Char.ToUpperInvariant p.[0]) + p.Substring 1)
        |> String.concat ""

    let private fileOf (stem: string) (suffix: string) = $"{root}/{stem}/{stem}.{suffix}.g.cs"

    /// The relative path (to the work dir) a generated file is written to: `out/<Repo>/<file>`.
    let outPath (repo: string) (file: string) = $"out/{repo}/{file}"

    /// The generated C# targets of a handover. Only operation interfaces of code-backed elements count (events have no
    /// NSwag output). `csharpRepos` is the policy's list; None means every repo that serves an operation in a split run, and "." in a single-repo run.
    let targets (h: Handover) (csharpRepos: string list option) : Result<Target list, string list> =
        let specs = h.Elements |> List.map (fun e -> e.Id.Value, e) |> Map.ofList
        let split = h.Elements |> List.exists (fun e -> e.Repo.IsSome)
        let ends =
            [ for i in h.Interfaces do
                  if i.Kind = "operation" && (i.Role = "serves" || i.Role = "calls") then
                      match Map.tryFind i.Element specs, i.File with
                      | Some e, Some file ->
                          yield (defaultArg e.Repo "."), file, (if i.Role = "serves" then Server else Client)
                      | _ -> () ]
        let wanted =
            match csharpRepos with
            | Some repos -> Set.ofList repos
            | None when split -> ends |> List.filter (fun (_, _, s) -> s = Server) |> List.map (fun (r, _, _) -> r) |> Set.ofList
            | None -> Set.ofList [ "." ]
        let built =
            ends
            |> List.filter (fun (r, _, _) -> wanted.Contains r)
            |> List.groupBy (fun (r, f, _) -> r, f)
            |> List.map (fun ((repo, source), g) ->
                let sides = [ Server; Client ] |> List.filter (fun s -> g |> List.exists (fun (_, _, x) -> x = s))
                let st = stem source
                { Repo = repo
                  Source = source
                  Stem = st
                  Namespace = "Contracts." + st
                  Sides = sides
                  Files =
                    [ yield fileOf st "Dtos"
                      if List.contains Server sides then yield fileOf st "Server"
                      if List.contains Client sides then yield fileOf st "Client" ] })
            |> List.sortWith (fun a b ->
                match System.String.CompareOrdinal(a.Repo, b.Repo) with
                | 0 -> System.String.CompareOrdinal(a.Source, b.Source)
                | c -> c)
        let clashes =
            built
            |> List.groupBy (fun t -> t.Repo, t.Stem)
            |> List.filter (fun (_, g) -> g.Length > 1)
            |> List.map (fun ((repo, st), g) ->
                let repoName = if repo = "." then "the repo" else $"repo {repo}"
                let sources = g |> List.map (fun t -> t.Source) |> String.concat " and "
                $"contracts {sources} both generate '{st}' in {repoName}; rename one")
        if clashes.IsEmpty then Ok built else Error clashes

    /// One `nswag` command line per output file, input `in/<Source>`, output `out/<Repo>/<file>`, relative to the work dir.
    let commands (t: Target) : string list =
        let input = $"in/{t.Source}"
        let tail = $"/JsonLibrary:SystemTextJson /Namespace:{t.Namespace}"
        let line cmd flags file = $"nswag {cmd} /input:{input} /output:{outPath t.Repo file} {flags} {tail}"
        [ yield line "openapi2csclient" "/GenerateClientClasses:false /GenerateDtoTypes:true" (fileOf t.Stem "Dtos")
          if List.contains Server t.Sides then
              yield line "openapi2cscontroller" $"/ClassName:{t.Stem} /ControllerStyle:Partial /ControllerTarget:AspNetCore /GenerateDtoTypes:false" (fileOf t.Stem "Server")
          if List.contains Client t.Sides then
              yield line "openapi2csclient" $"/ClassName:{t.Stem}Client /GenerateClientInterfaces:true /GenerateDtoTypes:false /InjectHttpClient:true /UseBaseUrl:false" (fileOf t.Stem "Client") ]

    /// baseline and current map repo-relative path to sha256. Every path under `root` counts, added ones too:
    /// a new file next to generated code is a way to change what the build sees.
    let drift (baseline: Map<string, string>) (current: Map<string, string>) : string list =
        let under (p: string) = p.StartsWith(root + "/", System.StringComparison.Ordinal)
        [ for KeyValue(p, h) in baseline do
              if under p then
                  match Map.tryFind p current with
                  | None -> yield $"missing: {p}"
                  | Some h2 when h2 <> h -> yield $"modified: {p}"
                  | _ -> ()
          for KeyValue(p, _) in current do
              if under p && not (baseline.ContainsKey p) then yield $"added: {p}" ]
        |> List.sort
