namespace Factory.Core

open System
open System.Text.Json
open System.Text.RegularExpressions

/// One deliberate wrong turn on a placement: a token prop set to another value of its group, or the whole element removed.
/// A UI mutant is killed when the page tests fail on it; one that passes says the tests do not look at what it changed.
type UiMutant =
    { Id: string
      Placement: string
      Kind: string // "token" | "remove"
      Prop: string option
      From: string option
      To: string option }

[<RequireQualifiedAccess>]
module UiMutants =

    let private str (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    /// Values of a token group: the keys that do not start with `$`, in file order.
    let private groupValues (tokensJson: string) (group: string) =
        use d = JsonDocument.Parse tokensJson
        match d.RootElement.TryGetProperty group with
        | true, g when g.ValueKind = JsonValueKind.Object ->
            [ for p in g.EnumerateObject() do
                  if not (p.Name.StartsWith "$") then yield p.Name ]
        | _ -> []

    /// Mutants from the spec's placements, the component manifests and the frozen tokens (W3C JSON), in spec order. The notes say what could not be generated.
    let generate (specJson: string) (manifestJsons: string list) (tokensJson: string option) : UiMutant list * string list =
        use spec = JsonDocument.Parse specJson
        let tokenProps =
            manifestJsons
            |> List.collect (fun m ->
                use d = JsonDocument.Parse m
                let name = str d.RootElement "name" |> Option.defaultValue ""
                match d.RootElement.TryGetProperty "props" with
                | true, ps when ps.ValueKind = JsonValueKind.Object ->
                    [ for p in ps.EnumerateObject() do
                          if p.Value.ValueKind = JsonValueKind.Object && str p.Value "type" = Some "token" then
                              match str p.Value "token" with
                              | Some g -> yield (name, p.Name), g
                              | None -> () ]
                | _ -> [])
            |> Map.ofList
        let notes = ResizeArray<string>()
        if tokensJson.IsNone && not tokenProps.IsEmpty then notes.Add "no design tokens were frozen, so no token mutants were generated"
        let out = ResizeArray<UiMutant>()
        match spec.RootElement.TryGetProperty "slots" with
        | true, slots when slots.ValueKind = JsonValueKind.Object ->
            for slot in slots.EnumerateObject() do
                if slot.Value.ValueKind = JsonValueKind.Array then
                    for p in slot.Value.EnumerateArray() do
                        match str p "id", str p "component" with
                        | Some id, Some comp ->
                            match p.TryGetProperty "props" with
                            | true, props when props.ValueKind = JsonValueKind.Object && tokensJson.IsSome ->
                                for prop in props.EnumerateObject() do
                                    match Map.tryFind (comp, prop.Name) tokenProps with
                                    | Some group when prop.Value.ValueKind = JsonValueKind.String ->
                                        let current = prop.Value.GetString()
                                        for v in groupValues tokensJson.Value group do
                                            if v <> current then
                                                out.Add { Id = $"{id}:{prop.Name}:{current}->{v}"; Placement = id; Kind = "token"
                                                          Prop = Some prop.Name; From = Some current; To = Some v }
                                    | _ -> ()
                            | _ -> ()
                            out.Add { Id = $"{id}:remove"; Placement = id; Kind = "remove"; Prop = None; From = None; To = None }
                        | _ -> ()
        | _ -> ()
        List.ofSeq out, List.ofSeq notes

    /// Index of the `>` that closes the opening tag starting at `start` (a `<`), skipping braces and quotes.
    let private openingEnd (code: string) (start: int) : int option =
        let mutable i = start
        let mutable depth = 0
        let mutable quote = '\000'
        let mutable result = None
        while result.IsNone && i < code.Length do
            let c = code[i]
            if quote <> '\000' then (if c = quote then quote <- '\000')
            elif c = '"' || c = '\'' || (c = '`' && depth > 0) then quote <- c
            elif c = '{' then depth <- depth + 1
            elif c = '}' then depth <- depth - 1
            elif c = '>' && depth = 0 && not (i > 0 && code[i - 1] = '=') then result <- Some i
            i <- i + 1
        result

    /// Start index and tag of the element carrying data-ui-id="<id>": the nearest `<Tag` before it.
    let private locate (code: string) (id: string) =
        let m = Regex.Match(code, $"""data-ui-id=\{{?["']{Regex.Escape id}["']\}}?""")
        if not m.Success then None
        else
            Regex.Matches(code, @"<([A-Za-z][A-Za-z0-9.]*)\b")
            |> Seq.filter (fun t -> t.Index < m.Index)
            |> Seq.tryLast
            |> Option.map (fun t -> t.Index, t.Groups[1].Value)

    /// The page source with the mutant applied, or why it was skipped. Never throws on odd source.
    let apply (code: string) (m: UiMutant) : Result<string, string> =
        match locate code m.Placement with
        | None -> Error "skipped: placement not found in the page"
        | Some(start, tag) ->
            match openingEnd code start with
            | None -> Error "skipped: cannot find the element's end"
            | Some gt ->
                let opening = code.Substring(start, gt - start + 1)
                match m.Kind with
                | "token" ->
                    let prop, from, target = m.Prop.Value, m.From.Value, m.To.Value
                    let rx = Regex($"""\b{Regex.Escape prop}=(?:"{Regex.Escape from}"|'{Regex.Escape from}'|\{{\s*["']{Regex.Escape from}["']\s*\}})""")
                    if rx.IsMatch opening then
                        Ok(code.Substring(0, start) + rx.Replace(opening, $"{prop}=\"{target}\"", 1) + code.Substring(gt + 1))
                    else Error "skipped: not a literal"
                | _ ->
                    let selfClosing = code[gt - 1] = '/'
                    let finish =
                        if selfClosing then Some(gt + 1)
                        else
                            let rx = Regex($@"<{Regex.Escape tag}(?![A-Za-z0-9.])|</{Regex.Escape tag}\s*>")
                            let mutable depth = 1
                            let mutable pos = gt + 1
                            let mutable endAt = None
                            while endAt.IsNone && depth > 0 do
                                let n = rx.Match(code, pos)
                                if not n.Success then depth <- -1
                                elif n.Value.StartsWith "</" then
                                    depth <- depth - 1
                                    pos <- n.Index + n.Length
                                    if depth = 0 then endAt <- Some pos
                                else
                                    match openingEnd code n.Index with
                                    | Some g ->
                                        if code[g - 1] <> '/' then depth <- depth + 1
                                        pos <- g + 1
                                    | None -> depth <- -1
                            endAt
                    match finish with
                    | None -> Error "skipped: cannot find the element's end"
                    | Some e ->
                        let before = code.Substring(0, start).TrimEnd()
                        let after = code.Substring(e).TrimStart()
                        // An element that is the whole value of a `{...}` becomes `null`; an empty `{}` is not valid JSX.
                        if before.EndsWith "{" && after.StartsWith "}" then Ok(code.Substring(0, start) + "null" + code.Substring e)
                        else Ok(code.Substring(0, start) + code.Substring e)
