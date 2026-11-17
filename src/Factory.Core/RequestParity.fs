namespace Factory.Core

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions

/// A JSON body as its shape: keys kept, each leaf the format shape of its text, an array as its first element (how many
/// records there are is data). Mirrors designc's `Parity.jsonShape`, so a baseline row and a logged request compare.
type JsonShape =
    | Leaf of string
    | Obj of Map<string, JsonShape>
    | Arr of JsonShape option

/// A port of designc's `Parity.shape` and `Parity.sameShape` (renderer/parity.js). designc owns the contract; docs/parity-cases.json
/// and the shapes in docs/parity-requests.example.json are the tests. Digits are ASCII only and whitespace is JavaScript's.
[<RequireQualifiedAccess>]
module Shape =
    // JavaScript's \s: ASCII whitespace plus the Unicode space separators, BOM, line and paragraph separators.
    let private ws = @"[\t\n\v\f\r    -     　﻿]"
    let private clean (s: string) = Regex.Replace((if isNull s then "" else s), ws + "+", " ").Trim([| ' ' |])

    let private runShape (r: string) =
        if r.Length = 1 then "d"
        elif r.Length = 2 then (if r.[0] = '0' then "0d" else "dd")
        else String('d', r.Length)

    let private date = Regex($@"^([0-9]{{1,4}})([/.\-])([0-9]{{1,2}})\2([0-9]{{1,4}})(?:[ T]([0-9]{{1,2}}):([0-9]{{2}})(?::([0-9]{{2}}))?)?$")
    let private time = Regex(@"^([0-9]{1,2}):([0-9]{2})(?::([0-9]{2}))?$")
    let private number = Regex($@"^([^0-9(\-]{{0,4}}?){ws}?(\(|-)?([^0-9(\-]{{0,4}}?)([0-9]{{1,3}}(?:,[0-9]{{3}})+|[0-9]+)(?:\.([0-9]+))?(\))?{ws}?([^0-9]{{0,4}})$")

    let private g (m: Match) (i: int) = if m.Groups.[i].Success then m.Groups.[i].Value else ""

    /// The format of a value, never the value: "15/01/2026" -> date:dd/0d/dddd, "1,000.00" -> num:#,##0.00, "Bound" -> text:Aa.
    let shape (text: string) : string =
        let t = clean text
        if t = "" then "empty"
        else
            let dm = date.Match t
            let tm = time.Match t
            let nm = number.Match t
            if dm.Success && ((g dm 1).Length = 4 || (g dm 4).Length >= 2) then
                let sep = g dm 2
                let tail =
                    if g dm 5 <> "" then " " + runShape (g dm 5) + ":" + runShape (g dm 6) + (if g dm 7 <> "" then ":" + runShape (g dm 7) else "")
                    else ""
                "date:" + runShape (g dm 1) + sep + runShape (g dm 3) + sep + runShape (g dm 4) + tail
            elif tm.Success then
                let secs = if g tm 3 <> "" then ":" + runShape (g tm 3) else ""
                "time:" + runShape (g tm 1) + ":" + runShape (g tm 2) + secs
            elif nm.Success
                 && not (Regex.IsMatch(g nm 1 + g nm 3 + g nm 7, "[A-Za-z]{5}"))
                 && ((g nm 2 = "(") = nm.Groups.[6].Success) then
                let int = g nm 4
                let group = if int.Contains ',' then "#,##0" elif int.Length >= 4 then "0" else "?0"
                let body = group + (if nm.Groups.[5].Success then "." + String('0', (g nm 5).Length) else "")
                let neg = if g nm 2 = "(" then "(" + body + ")" elif g nm 2 = "-" then "-" + body else body
                "num:" + g nm 1 + g nm 3 + neg + g nm 7
            else
                let letters = Regex.Replace(t, "[^A-Za-z]", "")
                if letters = "" then "digits:" + Regex.Replace(t, "[0-9]", "d")
                elif letters.Length = 1 then "text:A?"
                elif letters = letters.ToUpperInvariant() then "text:AA"
                elif letters = letters.ToLowerInvariant() then "text:aa"
                else
                    let words = Regex.Split(t, ws + "+") |> Array.filter (fun w -> Regex.IsMatch(w, "[A-Za-z]"))
                    let first = Regex.Replace(words.[0], "[^A-Za-z]", "")
                    if first.[0] = Char.ToUpperInvariant first.[0] && first.Substring 1 = (first.Substring 1).ToLowerInvariant() then "text:Aa" else "text:aA"

    let private runsCompatible (a: string) (b: string) =
        a = b || (a = "dd" && (b = "0d" || b = "d")) || (b = "dd" && (a = "0d" || a = "d"))

    let private split (v: string) = Regex.Split(v, @"([/.\-: ])")
    let private numParts = Regex(@"^(.*?)(\(|-)?(#,##0|\?0|0)(\.0+)?\)?(.*)$")

    /// Two shapes could be the same format. Unknown grouping (?0) fits either; a sign is compared only when both are negative;
    /// an empty value fits anything.
    let sameShape (a: string) (b: string) : bool =
        if a = b || a = "empty" || b = "empty" then true
        else
            let cut (s: string) = let i = s.IndexOf ':' in (s.Substring(0, max i 0) |> fun k -> if i < 0 then "" else k), s.Substring(i + 1)
            let ka, va = cut a
            let kb, vb = cut b
            if ka <> kb then false
            elif ka = "text" then va = "A?" || vb = "A?" || va = vb
            elif ka = "date" || ka = "time" then
                let ra, rb = split va, split vb
                ra.Length = rb.Length && Array.forall2 runsCompatible ra rb
            elif ka = "num" then
                let parse v =
                    let m = numParts.Match v
                    if m.Success then Some(g m 1, g m 2, g m 3, g m 4, g m 5) else None
                match parse va, parse vb with
                | Some(pa, na, ga, da, qa), Some(pb, nb, gb, db, qb) ->
                    if pa <> pb || qa <> qb || da <> db then false
                    elif ga <> gb && ga <> "?0" && gb <> "?0" then false
                    else not (na <> "" && nb <> "" && na <> nb)
                | _ -> va = vb
            else va = vb

/// A request log and a legacy request list, as shapes and compared (the pure part of the F9 request check).
[<RequireQualifiedAccess>]
module JsonShapes =
    let private number (e: JsonElement) =
        let d = e.GetDouble()
        if Math.Abs d < 1e21 && d = Math.Floor d then d.ToString("0", CultureInfo.InvariantCulture) else d.ToString("R", CultureInfo.InvariantCulture)

    let rec ofElement (e: JsonElement) : JsonShape =
        match e.ValueKind with
        | JsonValueKind.Null | JsonValueKind.Undefined -> Leaf "null"
        | JsonValueKind.True | JsonValueKind.False -> Leaf "bool"
        | JsonValueKind.Number -> Leaf(Shape.shape (number e))
        | JsonValueKind.String -> Leaf(Shape.shape (e.GetString()))
        | JsonValueKind.Array -> Arr(e.EnumerateArray() |> Seq.tryHead |> Option.map ofElement)
        | _ -> Obj(e.EnumerateObject() |> Seq.map (fun p -> p.Name, ofElement p.Value) |> Map.ofSeq)

    /// A body's shape, None for no body. A body that is not JSON is the shape of its first 200 characters.
    let ofBody (text: string option) : JsonShape option =
        match text with
        | None -> None
        | Some t when String.IsNullOrEmpty t -> None
        | Some t ->
            try
                use d = JsonDocument.Parse t
                Some(ofElement d.RootElement)
            with :? JsonException -> Some(Leaf(Shape.shape (if t.Length > 200 then t.Substring(0, 200) else t)))

    /// A shape written as JSON in a baseline row (designc's docs/parity-requests.schema.json): a string, an array of zero or one, or an object.
    let rec ofShapeElement (e: JsonElement) : JsonShape =
        match e.ValueKind with
        | JsonValueKind.String -> Leaf(e.GetString())
        | JsonValueKind.Array -> Arr(e.EnumerateArray() |> Seq.tryHead |> Option.map ofShapeElement)
        | JsonValueKind.Object -> Obj(e.EnumerateObject() |> Seq.map (fun p -> p.Name, ofShapeElement p.Value) |> Map.ofSeq)
        | _ -> Leaf "null"

    let rec toJson (s: JsonShape) : string =
        match s with
        | Leaf v -> JsonSerializer.Serialize v
        | Arr None -> "[]"
        | Arr(Some x) -> "[" + toJson x + "]"
        | Obj m -> "{" + String.Join(",", m |> Map.toList |> List.map (fun (k, v) -> JsonSerializer.Serialize k + ":" + toJson v)) + "}"

/// One request the page made in a state (from the generated page test's log). `Body` is already a shape; no values reach this module.
type LoggedRequest = { Method: string; Path: string; Matched: bool; Body: JsonShape option }

/// One request the legacy app made (a row of the baseline's `requests`): path already templated, body as a shape.
type LegacyRequest = { Method: string; Path: string; Request: JsonShape option }

type RequestFindings = { Failures: string list; Notes: string list }

/// F9: the page may call only the endpoints its spec lists, must call each of them, and must send the legacy request's shape.
/// Pure. Findings name endpoints and shapes, never values.
[<RequireQualifiedAccess>]
module RequestParity =
    let private isId (s: string) =
        s <> "" && (s |> Seq.forall Char.IsAsciiDigit
                    || Regex.IsMatch(s, "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))

    /// A path without query, numeric and uuid segments as `:id` (designc's Parity.requests rule).
    let template (path: string) : string =
        let p = (path.Split('?').[0]).Split('/') |> Array.map (fun s -> if isId s then ":id" else s)
        String.Join("/", p)

    /// `GET /api/x/:id` as (method, path). Anything without a method is taken as GET.
    let parseEndpoint (s: string) : string * string =
        match s.Trim().Split([| ' ' |], 2, StringSplitOptions.RemoveEmptyEntries) with
        | [| m; p |] -> m.ToUpperInvariant(), p.Trim()
        | [| p |] -> "GET", p
        | _ -> "GET", ""

    /// Segments match when equal or when either side is a `:param`.
    let samePath (a: string) (b: string) : bool =
        let sa, sb = a.Split('/'), b.Split('/')
        sa.Length = sb.Length && Array.forall2 (fun (x: string) (y: string) -> x = y || x.StartsWith ":" || y.StartsWith ":") sa sb

    /// A spec path against a concrete one: only the spec's `:param` segments are wildcards.
    let specPath (spec: string) (actual: string) : bool =
        let sa, sb = spec.Split('/'), actual.Split('?').[0].Split('/')
        sa.Length = sb.Length && Array.forall2 (fun (x: string) (y: string) -> x = y || (x.StartsWith ":" && y <> "")) sa sb

    let rec sameJsonShape (a: JsonShape) (b: JsonShape) : bool =
        match a, b with
        | Leaf x, Leaf y -> Shape.sameShape x y
        | Arr None, Arr _ | Arr _, Arr None -> true
        | Arr(Some x), Arr(Some y) -> sameJsonShape x y
        | Obj x, Obj y -> x.Count = y.Count && x |> Map.forall (fun k v -> match Map.tryFind k y with Some w -> sameJsonShape v w | None -> false)
        | _ -> false

    let private label (m: string, p: string) = m + " " + p

    /// `legacy` is None when no baseline recorded requests: nothing is compared against legacy then.
    let compare (logs: LoggedRequest list) (specSources: string list) (legacy: LegacyRequest list option) : RequestFindings =
        let spec = specSources |> List.map parseEndpoint |> List.filter (fun (_, p) -> p <> "")
        let called = logs |> List.map (fun l -> l.Method.ToUpperInvariant(), l.Path.Split('?').[0]) |> List.distinct
        let listed (m, p) = spec |> List.exists (fun (sm, sp) -> sm = m && specPath sp p)
        let unlisted = called |> List.filter (listed >> not) |> List.map (fun e -> "unlisted request: " + label (fst e, template (snd e)) + " is not in the spec's data sources")
        let never =
            spec |> List.filter (fun (sm, sp) -> not (called |> List.exists (fun (m, p) -> m = sm && specPath sp p)))
            |> List.map (fun e -> "never called: " + label e + " is in the spec's data sources but no state requested it")
        let shapes, notes =
            match legacy with
            | None -> [], []
            | Some leg ->
                let legacyKey (r: LegacyRequest) = r.Method.ToUpperInvariant(), r.Path
                let bad =
                    [ for l in logs do
                        let key = l.Method.ToUpperInvariant(), template l.Path
                        match l.Body with
                        | Some mine ->
                            for r in leg do
                                let (rm, rp) = legacyKey r
                                if rm = fst key && samePath rp (snd key) then
                                    match r.Request with
                                    | Some theirs when not (sameJsonShape mine theirs) ->
                                        yield "request shape: " + label key + " sends " + JsonShapes.toJson mine + " but legacy sent " + JsonShapes.toJson theirs
                                    | None -> yield "request shape: " + label key + " sends a body but legacy sent none"
                                    | _ -> ()
                        | None -> () ]
                    |> List.distinct
                let dropped =
                    leg |> List.map legacyKey |> List.distinct
                    |> List.filter (fun (m, p) -> not (spec |> List.exists (fun (sm, sp) -> sm = m && samePath sp p)))
                    |> List.map (fun e -> "dropped: legacy called " + label e + " and the spec does not list it")
                bad, dropped
        { Failures = unlisted @ never @ shapes; Notes = notes }
