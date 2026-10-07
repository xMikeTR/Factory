namespace Factory.Core

open System

/// A repo-relative path or glob. Supports `*` and `?` inside a segment and `**` as a whole
/// segment. Character classes and brace expansion are rejected at construction, so every
/// PathPattern that exists is one the overlap and containment checks below understand.
[<CustomEquality; CustomComparison>]
type PathPattern =
    private { Raw: string; Segs: string list }

    member this.Value = this.Raw
    override this.ToString() = this.Raw
    override this.Equals(o) =
        match o with
        | :? PathPattern as p -> p.Raw = this.Raw
        | _ -> false
    override this.GetHashCode() = this.Raw.GetHashCode()
    interface IComparable with
        member this.CompareTo(o) =
            match o with
            | :? PathPattern as p -> String.CompareOrdinal(this.Raw, p.Raw)
            | _ -> invalidArg "o" "not a PathPattern"

[<RequireQualifiedAccess>]
module PathPattern =

    let private isWild (seg: string) = seg.IndexOf('*') >= 0 || seg.IndexOf('?') >= 0

    /// Parse and normalise. Rejects absolute paths, `..`, `.`, empty segments, `[...]`, `{...}`
    /// and `**` used inside a segment (`a**b`).
    let create (input: string) : Result<PathPattern, string> =
        if String.IsNullOrWhiteSpace input then Error "empty path"
        else
            let s = input.Trim().Replace('\\', '/')
            let s = if s.StartsWith "./" then s.Substring 2 else s
            if s.StartsWith "/" || (s.Length > 1 && s.[1] = ':') then Error $"absolute path not allowed: {input}"
            elif s.IndexOfAny [| '['; ']'; '{'; '}' |] >= 0 then Error $"character classes and braces are not supported: {input}"
            else
                let segs = s.TrimEnd('/').Split('/') |> List.ofArray
                match segs |> List.tryFind (fun g -> g = "" || g = "." || g = ".." || (g.Contains "**" && g <> "**")) with
                | Some bad -> Error $"invalid segment '{bad}' in {input}"
                | None -> Ok { Raw = String.Join("/", segs); Segs = segs }

    let createOrFail (input: string) =
        match create input with
        | Ok p -> p
        | Error e -> invalidArg "input" e

    let isExact (p: PathPattern) = p.Segs |> List.forall (isWild >> not)

    let literalPrefix (p: PathPattern) = p.Segs |> List.takeWhile (isWild >> not)

    let rec private matchSeg (pat: string) pi (s: string) si =
        if pi = pat.Length then si = s.Length
        elif pat.[pi] = '*' then matchSeg pat (pi + 1) s si || (si < s.Length && matchSeg pat pi s (si + 1))
        elif si < s.Length && (pat.[pi] = '?' || pat.[pi] = s.[si]) then matchSeg pat (pi + 1) s (si + 1)
        else false

    let rec private matchSegs (ps: string list) (ss: string list) =
        match ps, ss with
        | [], [] -> true
        | "**" :: rest, _ ->
            matchSegs rest ss || (match ss with _ :: st -> matchSegs ps st | [] -> false)
        | p :: prest, s :: srest -> matchSeg p 0 s 0 && matchSegs prest srest
        | _ -> false

    let private split (path: string) =
        let p = path.Replace('\\', '/')
        let p = if p.StartsWith "./" then p.Substring 2 else p
        p.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    /// Does the glob match a concrete repo-relative file path? Ordinal, case-sensitive.
    let matches (glob: PathPattern) (path: string) = matchSegs glob.Segs (split path)

    let rec private isPrefixOf (a: string list) (b: string list) =
        match a, b with
        | [], _ -> true
        | x :: xs, y :: ys when x = y -> isPrefixOf xs ys
        | _ -> false

    /// Could any file match both patterns?
    ///
    /// RESOURCES.md §1. Spec: tests "paths: overlap".
    ///   - exact vs exact: same segments
    ///   - exact vs glob: does the glob match the exact path? (precise)
    ///   - glob vs glob: walk both segment lists together; `**` matches any number of segments,
    ///     two single segments are compatible if a literal one matches the other's glob, and two
    ///     wild segments are assumed compatible (the over-approximation).
    ///   Why over-approximate is the SAFE direction here: a false "yes" only merges two teams.
    ///   A false "no" lets two agents write the same file in parallel.
    ///   Helpers available above: isExact, literalPrefix, matchSegs, isPrefixOf.
    let mayOverlap (a: PathPattern) (b: PathPattern) : bool =
        match isExact a, isExact b with
        | true, true -> a.Segs = b.Segs
        | true, false -> matchSegs b.Segs a.Segs
        | false, true -> matchSegs a.Segs b.Segs
        | false, false ->
            let segCompatible (x: string) (y: string) =
                match isWild x, isWild y with
                | false, _ -> matchSeg y 0 x 0
                | true, false -> matchSeg x 0 y 0
                | true, true -> true
            let rec overlap xs ys =
                match xs, ys with
                | [], [] -> true
                | "**" :: xt, _ -> overlap xt ys || (match ys with _ :: yt -> overlap xs yt | [] -> false)
                | _, "**" :: yt -> overlap xs yt || (match xs with _ :: xt -> overlap xt ys | [] -> false)
                | x :: xt, y :: yt -> segCompatible x y && overlap xt yt
                | _ -> false
            overlap a.Segs b.Segs

    /// Is every file matched by `inner` also matched by `outer`?
    ///
    /// RESOURCES.md §1. Spec: tests "paths: containment".
    ///   Here the safe direction is the opposite: under-approximate. A false "no" rejects a valid
    ///   plan (the planner retries); a false "yes" lets a task write outside its element.
    ///   - exact inner: does outer match it?
    ///   - outer of the form literal/.../** : is outer's base a prefix of inner's literal prefix?
    ///   - anything else: only if the patterns are identical.
    let isWithin (inner: PathPattern) (outer: PathPattern) : bool =
        if isExact inner then matchSegs outer.Segs inner.Segs
        else
            match List.rev outer.Segs with
            | "**" :: revBase when revBase |> List.forall (isWild >> not) ->
                isPrefixOf (List.rev revBase) (literalPrefix inner)
            | _ -> inner.Segs = outer.Segs
