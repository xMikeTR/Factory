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

    /// `*` any run of characters, `?` one character. Linear in practice: a failed attempt resumes at the
    /// last `*` one character further on, instead of trying every split (which is exponential for `*a*a*a*b`).
    let private matchSeg (pat: string) pi (s: string) si =
        let mutable p = pi
        let mutable i = si
        let mutable star = -1
        let mutable mark = 0
        let mutable ok = true
        let mutable finished = false
        while not finished do
            if i >= s.Length then finished <- true
            elif p < pat.Length && pat.[p] = '*' then
                star <- p
                mark <- i
                p <- p + 1
            elif p < pat.Length && (pat.[p] = '?' || pat.[p] = s.[i]) then
                p <- p + 1
                i <- i + 1
            elif star >= 0 then
                p <- star + 1
                mark <- mark + 1
                i <- mark
            else
                ok <- false
                finished <- true
        if not ok then false
        else
            while p < pat.Length && pat.[p] = '*' do p <- p + 1
            p = pat.Length

    /// Does the pattern's segment list match the path's? `**` takes any number of segments; the answer
    /// for each (pattern position, path position) is worked out once, so repeated `**` cannot blow up.
    let private matchSegs (ps: string list) (ss: string list) =
        let pa = Array.ofList ps
        let sa = Array.ofList ss
        let memo = System.Collections.Generic.Dictionary<struct (int * int), bool>()
        let rec go pi si =
            match memo.TryGetValue(struct (pi, si)) with
            | true, v -> v
            | _ ->
                let v =
                    if pi = pa.Length then si = sa.Length
                    elif pa.[pi] = "**" then go (pi + 1) si || (si < sa.Length && go pi (si + 1))
                    else si < sa.Length && matchSeg pa.[pi] 0 sa.[si] 0 && go (pi + 1) (si + 1)
                memo.[struct (pi, si)] <- v
                v
        go 0 0

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
    /// Spec: tests "paths: overlap".
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
            let xa = Array.ofList a.Segs
            let ya = Array.ofList b.Segs
            let memo = System.Collections.Generic.Dictionary<struct (int * int), bool>()
            // Same walk as before over array positions; each (i, j) is decided once, so repeated `**` cannot blow up.
            let rec overlap i j =
                match memo.TryGetValue(struct (i, j)) with
                | true, v -> v
                | _ ->
                    let v =
                        if i = xa.Length && j = ya.Length then true
                        elif i < xa.Length && xa.[i] = "**" then overlap (i + 1) j || (j < ya.Length && overlap i (j + 1))
                        elif j < ya.Length && ya.[j] = "**" then overlap i (j + 1) || (i < xa.Length && overlap (i + 1) j)
                        elif i < xa.Length && j < ya.Length then segCompatible xa.[i] ya.[j] && overlap (i + 1) (j + 1)
                        else false
                    memo.[struct (i, j)] <- v
                    v
            overlap 0 0

    /// Is every file matched by `inner` also matched by `outer`?
    ///
    /// Spec: tests "paths: containment".
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
