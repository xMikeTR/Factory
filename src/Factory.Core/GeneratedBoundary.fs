namespace Factory.Core

open System.Text.RegularExpressions

/// Hand-written boundary code the Factory already generated from a pinned contract (X3): an HTTP call to a pinned operation's path, or a DTO
/// that shadows a schema the repo has generated code for. Pure: it reads added lines and says what to use instead; what to do about it
/// (report, block) is the TeamRunner's.
[<RequireQualifiedAccess>]
module GeneratedBoundary =

    /// An operation of a pinned contract (`Source` is the contract file); `Id` is its operationId when the contract has one.
    type Op = { Source: string; Id: string option; Method: string; Path: string }

    type Finding =
        { File: string
          Line: int
          /// "client" | "dto"
          Kind: string
          /// The operation ids (or "METHOD /path") joined with " or ", or the DTO name.
          Subject: string
          /// The operations' methods and paths, for the line; empty for a DTO.
          Where: string
          /// The generated symbol to use instead.
          Hint: string }

    let private extensions = [ ".ts"; ".tsx"; ".js"; ".jsx"; ".mjs"; ".cjs"; ".cs" ]

    let private judged (file: string) =
        not (file.StartsWith("generated/", System.StringComparison.Ordinal))
        && extensions |> List.exists (fun e -> file.EndsWith(e, System.StringComparison.OrdinalIgnoreCase))

    let private isCs (file: string) = file.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase)

    let private tsMarkers = [ "fetch("; "axios"; "XMLHttpRequest"; ".request("; ".get("; ".post("; ".put("; ".patch("; ".delete(" ]

    let private csMarkers =
        [ "HttpClient"; "HttpRequestMessage"; "RestRequest"; "GetAsync("; "PostAsync("; "PutAsync("; "PatchAsync("; "DeleteAsync("
          "SendAsync("; "GetFromJsonAsync"; "PostAsJsonAsync"; "PutAsJsonAsync"; "GetStringAsync(" ]

    let private literal = Regex(@"""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)'|`((?:[^`\\]|\\.)*)`", RegexOptions.Compiled)

    let private literals (text: string) =
        [ for m in literal.Matches text do
              yield [ 1; 2; 3 ] |> List.map (fun g -> m.Groups.[g]) |> List.find (fun g -> g.Success) |> fun g -> g.Value ]

    let private param = @"(?:\$\{[^}]*\}|[^/""'`?#\s]+)"
    let private isParam (seg: string) = seg.StartsWith "{" && seg.EndsWith "}"

    /// The regexes a string literal must match to name this path template: the whole template (optionally with a base URL before it and
    /// a `?query` after it), and the static head followed by `/` when the template ends in parameters (`"orders/" + id`).
    /// Empty for a template with no static segment.
    let private templates (path: string) : Regex list =
        let segs = path.Split([| '/' |], System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList
        if segs |> List.forall isParam then []
        else
            let part (s: string) = if isParam s then param else Regex.Escape s
            let full = "^(?:.*/)?" + (segs |> List.map part |> String.concat "/") + "/?(?:[?#].*)?$"
            let head = segs |> List.takeWhile (isParam >> not)
            let trailingParams = segs.Length > head.Length && segs |> List.skip head.Length |> List.forall isParam
            [ yield Regex(full)
              if trailingParams then yield Regex("^(?:.*/)?" + (head |> List.map part |> String.concat "/") + "/$") ]

    let private clientHint (op: Op) (cs: bool) =
        let st = Codegen.stem op.Source
        if cs then $"use `I{st}Client` from `{Codegen.root}/{st}/{st}.Client.g.cs`"
        else
            match op.Id with
            | Some id -> $"use `create{st}Client(baseUrl).{id}` from `{Codegen.tsRoot}/{st}/{st}.client.ts`"
            | None -> $"use the generated client for {op.Method} {op.Path} from `{Codegen.tsRoot}/{st}/{st}.client.ts`"

    let private dtoHint (source: string) (name: string) (cs: bool) =
        let st = Codegen.stem source
        if cs then $"use `Contracts.{st}.{name}` from `{Codegen.root}/{st}/{st}.Dtos.g.cs`"
        else $"use `components[\"schemas\"][\"{name}\"]` from `{Codegen.tsRoot}/{st}/{st}.schema.ts`"

    let private tsDecl = Regex(@"^\s*(?:export\s+)?(?:declare\s+)?(?:interface|type|class)\s+(\w+)\b", RegexOptions.Compiled)
    let private csDecl = Regex(@"\b(?:class|record|struct|interface)\s+(\w+)\b", RegexOptions.Compiled)

    let private opName (o: Op) = defaultArg o.Id (o.Method + " " + o.Path)

    /// The hand-written boundary code in `added` (file, [(line, text)]), sorted by file, then line. `ops` are the operations of the pinned contracts
    /// the repo has generated code for; `dtos` are (source, schema name) for the sources whose generated code in this repo has no server side
    /// (a server's own domain `Order` is legitimate).
    let findings (ops: Op list) (dtos: (string * string) list) (added: (string * (int * string) list) list) : Finding list =
        let matchers = ops |> List.map (fun o -> o, templates o.Path) |> List.filter (fun (_, t) -> not t.IsEmpty)
        [ for file, lines in added do
              if judged file then
                  let cs = isCs file
                  let markers = if cs then csMarkers else tsMarkers
                  for n, text in lines do
                      let client =
                          if markers |> List.exists (fun m -> text.Contains(m, System.StringComparison.Ordinal)) then
                              let lits = literals text
                              matchers |> List.filter (fun (_, ts) -> lits |> List.exists (fun l -> ts |> List.exists (fun t -> t.IsMatch l))) |> List.map fst
                          else []
                      if not client.IsEmpty then
                          yield
                              { File = file; Line = n; Kind = "client"
                                Subject = client |> List.map opName |> List.distinct |> String.concat " or "
                                Where = client |> List.map (fun o -> $"{o.Method} {o.Path}") |> List.distinct |> String.concat ", "
                                Hint = client |> List.map (fun o -> clientHint o cs) |> List.distinct |> String.concat " or " }
                      let decl = (if cs then csDecl else tsDecl).Match text
                      if decl.Success then
                          match dtos |> List.filter (fun (_, name) -> name = decl.Groups.[1].Value) with
                          | (source, name) :: _ ->
                              yield { File = file; Line = n; Kind = "dto"; Subject = name; Where = ""; Hint = dtoHint source name cs }
                          | [] -> () ]
        |> List.sortWith (fun a b ->
            match System.String.CompareOrdinal(a.File, b.File) with
            | 0 -> compare a.Line b.Line
            | c -> c)

    /// `<file>:<line>: hand-written client call for listOrders (GET /orders); use ...` or `... DTO `Order`; use ...`.
    let line (f: Finding) : string =
        let what = if f.Kind = "dto" then $"DTO `{f.Subject}`" else $"client call for {f.Subject} ({f.Where})"
        $"{f.File}:{f.Line}: hand-written {what}; {f.Hint}"
