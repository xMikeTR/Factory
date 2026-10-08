namespace Factory.Core

open System
open System.IO
open System.Text.Json.Nodes

/// What an agent may change in the files `npm ci` reads before the factory restores with the network on.
/// Same shape as ProjectFileChange: what the repo already had is trusted, only entries that are new or
/// changed against `before` are checked. Dependencies must come from the registry (no git, url, file, link or
/// alias specs), install-time lifecycle scripts cannot be added, and a lockfile may only resolve from the
/// pinned registry. The registry itself is fixed by the image, so `.npmrc` cannot change.
[<RequireQualifiedAccess>]
module PackageJsonChange =

    let registry = "https://registry.npmjs.org/"

    let private fileName (path: string) = Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant()

    /// Whether a repo path is read by `npm ci`.
    let affects (path: string) =
        match fileName path with
        | "package.json" | "package-lock.json" | ".npmrc" -> true
        | _ -> false

    let private lifecycle =
        set [ "preinstall"; "install"; "postinstall"; "prepublish"; "preprepare"; "prepare"; "postprepare"
              "prepublishonly"; "prepack"; "postpack"; "dependencies" ]

    let private depSections = [ "dependencies"; "devDependencies"; "optionalDependencies"; "peerDependencies" ]

    let private fromElsewhere (spec: string) =
        let s = spec.Trim().ToLowerInvariant()
        s.Contains "://" || s.StartsWith "git" || s.StartsWith "github:" || s.StartsWith "gitlab:" || s.StartsWith "bitbucket:"
        || s.StartsWith "file:" || s.StartsWith "link:" || s.StartsWith "npm:" || s.StartsWith "workspace:" || s.StartsWith "portal:"
        || (s.Contains "/" && not (s.StartsWith "@") && not (s.Contains " ")) // user/repo shorthand

    let private parse (text: string) : Result<JsonNode, string> =
        try
            match JsonNode.Parse text with
            | null -> Error "empty JSON"
            | n -> Ok n
        with e -> Error $"not valid JSON: {e.Message}"

    let private entries (n: JsonNode) : (string * JsonNode) list =
        match n with
        | :? JsonObject as o -> [ for kv in o -> kv.Key, kv.Value ]
        | _ -> []

    let private text (n: JsonNode) = if isNull n then "" else n.ToJsonString()

    /// Entries of `section` in `after` that are not identical in `before`.
    let private changed (before: JsonNode option) (after: JsonNode) (section: string) =
        let get (n: JsonNode) = match n with :? JsonObject as o when o.ContainsKey section -> o.[section] | _ -> null
        let old = before |> Option.map (get >> entries >> Map.ofList) |> Option.defaultValue Map.empty
        entries (get after) |> List.filter (fun (k, v) -> match Map.tryFind k old with Some o -> text o <> text v | None -> true)

    let rec private overrideErrors (path: string) (key: string, v: JsonNode) : string list =
        match v with
        | :? JsonObject -> entries v |> List.collect (fun (k, c) -> overrideErrors path (k, c))
        | _ ->
            let spec = if isNull v then "" else v.ToString()
            if fromElsewhere spec then [ $"{path}: override {key} = {spec} does not come from the registry" ] else []

    let private packageJson (path: string) (before: string option) (after: string) : string list =
        match parse after with
        | Error e -> [ $"{path}: {e}" ]
        | Ok doc ->
            let old = before |> Option.bind (parse >> Result.toOption)
            let deps =
                depSections
                |> List.collect (fun s ->
                    changed old doc s
                    |> List.choose (fun (k, v) ->
                        let spec = if isNull v then "" else v.ToString()
                        if fromElsewhere spec then Some $"{path}: {s}.{k} = {spec} does not come from the registry (use a version range; git, url, file, link and alias specs are refused)" else None))
            let scripts =
                changed old doc "scripts"
                |> List.choose (fun (k, _) ->
                    if Set.contains (k.ToLowerInvariant()) lifecycle then Some $"{path}: script {k} runs at install time and cannot be added or changed in a task" else None)
            let overrides = [ "overrides"; "resolutions" ] |> List.collect (fun s -> changed old doc s |> List.collect (overrideErrors path))
            let bundled =
                [ "bundleDependencies"; "bundledDependencies" ]
                |> List.collect (fun s ->
                    let get (n: JsonNode) = match n with :? JsonObject as o when o.ContainsKey s -> text o.[s] | _ -> ""
                    if get doc = "" || get doc = (old |> Option.map get |> Option.defaultValue "") then []
                    else [ $"{path}: {s} ships packages that skip the registry" ])
            deps @ scripts @ overrides @ bundled

    let private lockfile (path: string) (before: string option) (after: string) : string list =
        match parse after with
        | Error e -> [ $"{path}: {e}" ]
        | Ok doc ->
            let old = before |> Option.bind (parse >> Result.toOption)
            changed old doc "packages"
            |> List.choose (fun (k, v) ->
                match v with
                | :? JsonObject as o when o.ContainsKey "resolved" && not (isNull o.["resolved"]) ->
                    let url = o.["resolved"].ToString()
                    if url.StartsWith registry then None else Some $"{path}: {k} resolves from {url}, not from the pinned registry {registry}"
                | _ -> None)

    /// The rule for one changed npm restore input. `after` is None when the file was deleted, which is always fine.
    let review (path: string) (before: string option) (after: string option) : Result<unit, string list> =
        match after with
        | None -> Ok()
        | Some _ when fileName path = ".npmrc" -> Error [ $"{path}: the registry is fixed by the factory; .npmrc cannot change" ]
        | Some text ->
            let errors =
                match fileName path with
                | "package.json" -> packageJson path before text
                | "package-lock.json" -> lockfile path before text
                | _ -> []
            if errors.IsEmpty then Ok() else Error errors
