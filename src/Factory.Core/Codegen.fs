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

    /// npm `openapi-typescript`. The runner image pins the same number (docker/runner/Dockerfile).
    let openapiTypescriptVersion = "7.4.4"

    /// Everything the Factory generates lives under here and is its own: a seed replaces the whole folder, drift judges all of it.
    let area = "generated/contracts"

    /// Where generated TypeScript lives in a repo.
    let tsRoot = "generated/contracts/ts"

    type Language =
        | CSharp
        | TypeScript

    /// One called operation, as the Factory-rendered TS client needs it.
    type ClientOp = { Id: string; Method: string; Path: string }

    type Target =
        { Repo: string
          Source: string
          Stem: string
          Namespace: string
          Sides: Side list
          Files: string list
          Language: Language
          Operations: ClientOp list }

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
                      if List.contains Client sides then yield fileOf st "Client" ]
                  Language = CSharp
                  Operations = [] })
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

    let private tsFile (stem: string) (suffix: string) = $"{tsRoot}/{stem}/{stem}.{suffix}.ts"

    /// The generated TypeScript targets. `tsRepos` is the policy's list (exactly those repos); None means, in a split run,
    /// every repo that calls an operation and is in `nodeRepos`, and in a single-repo run "." when it is in `nodeRepos`.
    let tsTargets (h: Handover) (tsRepos: string list option) (nodeRepos: Set<string>) : Result<Target list, string list> =
        let specs = h.Elements |> List.map (fun e -> e.Id.Value, e) |> Map.ofList
        let split = h.Elements |> List.exists (fun e -> e.Repo.IsSome)
        let ends =
            [ for i in h.Interfaces do
                  if i.Kind = "operation" && (i.Role = "serves" || i.Role = "calls") then
                      match Map.tryFind i.Element specs, i.File with
                      | Some e, Some file -> yield (defaultArg e.Repo "."), file, i
                      | _ -> () ]
        let wanted =
            match tsRepos with
            | Some repos -> Set.ofList repos
            | None when split ->
                ends |> List.filter (fun (r, _, i) -> i.Role = "calls" && nodeRepos.Contains r) |> List.map (fun (r, _, _) -> r) |> Set.ofList
            | None -> if nodeRepos.Contains "." then Set.ofList [ "." ] else Set.empty
        let built =
            ends
            |> List.filter (fun (r, _, _) -> wanted.Contains r)
            |> List.groupBy (fun (r, f, _) -> r, f)
            |> List.map (fun ((repo, source), g) ->
                let st = stem source
                let ops =
                    g
                    |> List.choose (fun (_, _, i) ->
                        match i.Role, i.Method, i.Path with
                        | "calls", Some m, Some p -> Some { Id = i.Id; Method = m.ToUpperInvariant(); Path = p }
                        | _ -> None)
                    |> List.distinctBy (fun o -> o.Id)
                    |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Id, b.Id))
                let sides = [ Server; Client ] |> List.filter (fun s -> g |> List.exists (fun (_, _, i) -> (i.Role = "serves") = (s = Server)))
                { Repo = repo
                  Source = source
                  Stem = st
                  Namespace = ""
                  Sides = sides
                  Files = [ yield tsFile st "schema"; if not ops.IsEmpty then yield tsFile st "client" ]
                  Language = TypeScript
                  Operations = ops })
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

    /// The files the external tool writes (the Factory renders the rest): C# all of them, TS the schema only.
    let toolFiles (t: Target) : string list =
        match t.Language with
        | CSharp -> t.Files
        | TypeScript -> t.Files |> List.filter (fun f -> f.EndsWith(".schema.ts", System.StringComparison.Ordinal))

    let private jsonString (s: string) : string = System.Text.Json.JsonSerializer.Serialize s

    let private identifier = System.Text.RegularExpressions.Regex("^[A-Za-z_$][A-Za-z0-9_$]*$")

    /// The Factory's own thin fetch client, a pure function of the pinned operations, typed against openapi-typescript's `operations`.
    let tsClient (t: Target) (sourceSha256: string) : string =
        let sha = if sourceSha256.Length > 12 then sourceSha256.Substring(0, 12) else sourceSha256
        let key (id: string) = if identifier.IsMatch id then id else jsonString id
        let fns =
            t.Operations
            |> List.map (fun o ->
                let op = $"operations[{jsonString o.Id}]"
                $"    {key o.Id}: (args: Args<{op}>) => call(fetchImpl, baseUrl, {jsonString o.Method}, {jsonString o.Path}, args as never) as Promise<Ok<{op}>>,")
        let header =
            [ $"// Generated by the Factory from {t.Source} (sha256 {sha}). Do not edit: generated/contracts/** is read-only."
              $"import type {{ operations }} from \"./{t.Stem}.schema\";"
              "type Json<T> = T extends { content: { \"application/json\": infer B } } ? B : undefined;"
              "type Ok<O> = O extends { responses: infer R } ? { [S in keyof R]: S extends 200 | 201 | 202 | 204 | \"2XX\" ? Json<R[S]> : never }[keyof R] : never;"
              "type Args<O> = (O extends { parameters: infer P } ? { [K in keyof P as P[K] extends never | undefined ? never : K]: P[K] } : {})"
              "  & (O extends { requestBody: { content: { \"application/json\": infer B } } } ? { body: B }"
              "    : O extends { requestBody?: infer R } ? ([NonNullable<R>] extends [never] ? {} : NonNullable<R> extends { content: { \"application/json\": infer B } } ? { body?: B } : {}) : {});"
              "export class ApiError extends Error { constructor(readonly status: number, readonly body: unknown) { super(`HTTP ${status}`); } }"
              "async function call(f: typeof fetch, base: string, method: string, template: string, args: { path?: object; query?: object; body?: unknown }): Promise<unknown> {"
              "  const pathArgs = (args.path ?? {}) as Record<string, unknown>;"
              "  let url = base.replace(/\\/+$/, \"\") + template.replace(/\\{([^}]+)\\}/g, (_m, name: string) => encodeURIComponent(String(pathArgs[name])));"
              "  const params = new URLSearchParams();"
              "  for (const [k, v] of Object.entries((args.query ?? {}) as Record<string, unknown>)) {"
              "    if (v === undefined) continue;"
              "    for (const item of Array.isArray(v) ? v : [v]) params.append(k, String(item));"
              "  }"
              "  const qs = params.toString();"
              "  if (qs) url += \"?\" + qs;"
              "  const headers: Record<string, string> = { accept: \"application/json\" };"
              "  const init: RequestInit = { method, headers };"
              "  if (args.body !== undefined) { headers[\"content-type\"] = \"application/json\"; init.body = JSON.stringify(args.body); }"
              "  const res = await f(url, init);"
              "  const text = await res.text();"
              "  const type = res.headers.get(\"content-type\") ?? \"\";"
              "  const data: unknown = type.includes(\"json\") && text.length > 0 ? JSON.parse(text) : text.length > 0 ? text : undefined;"
              "  if (!res.ok) throw new ApiError(res.status, data);"
              "  return data;"
              "}"
              $"export function create{t.Stem}Client(baseUrl: string, fetchImpl: typeof fetch = fetch) {{"
              "  return {" ]
        String.concat "\n" (header @ fns @ [ "  };"; "}" ]) + "\n"

    /// One `nswag` command line per output file, input `in/<Source>`, output `out/<Repo>/<file>`, relative to the work dir.
    let commands (t: Target) : string list =
        match t.Language with
        | TypeScript ->
            let schema = outPath t.Repo (tsFile t.Stem "schema")
            [ $"openapi-typescript in/{t.Source} --output {schema}" ]
        | CSharp ->
            let input = $"in/{t.Source}"
            let tail = $"/JsonLibrary:SystemTextJson /Namespace:{t.Namespace}"
            let line cmd flags file = $"nswag {cmd} /input:{input} /output:{outPath t.Repo file} {flags} {tail}"
            [ yield line "openapi2csclient" "/GenerateClientClasses:false /GenerateDtoTypes:true" (fileOf t.Stem "Dtos")
              if List.contains Server t.Sides then
                  yield line "openapi2cscontroller" $"/ClassName:{t.Stem} /ControllerStyle:Partial /ControllerTarget:AspNetCore /GenerateDtoTypes:false" (fileOf t.Stem "Server")
              if List.contains Client t.Sides then
                  yield line "openapi2csclient" $"/ClassName:{t.Stem}Client /GenerateClientInterfaces:true /GenerateDtoTypes:false /InjectHttpClient:true /UseBaseUrl:false" (fileOf t.Stem "Client") ]

    /// baseline and current map repo-relative path to sha256. Every path under `area` counts, added ones too:
    /// a new file next to generated code is a way to change what the build sees.
    let drift (baseline: Map<string, string>) (current: Map<string, string>) : string list =
        let under (p: string) = p.StartsWith(area + "/", System.StringComparison.Ordinal)
        [ for KeyValue(p, h) in baseline do
              if under p then
                  match Map.tryFind p current with
                  | None -> yield $"missing: {p}"
                  | Some h2 when h2 <> h -> yield $"modified: {p}"
                  | _ -> ()
          for KeyValue(p, _) in current do
              if under p && not (baseline.ContainsKey p) then yield $"added: {p}" ]
        |> List.sort
