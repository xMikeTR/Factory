namespace Factory.Core

/// What a reviewer may find. Everything but `Design` blocks a task; `Design` is advisory.
type FindingCategory =
    /// The implementation special-cases the tests or otherwise games the checks.
    | TestGaming
    /// The implementation does not do what the task and its requirement ask.
    | Intent
    | BoundDecision
    | Security
    /// The tests do not pin the behaviour the task asks for (goes back to the test author).
    | WeakTests
    | Design

/// A review finding. It must cite a line inside the diff, so it can be checked and acted on.
type Finding =
    { Category: FindingCategory
      File: string
      Line: int
      Message: string }

/// Whether the reviewer's model family differs from the implementer's; stamped on every review.
type Independence =
    | Independent
    | SameFamily

/// One review round's outcome: what blocks, what is only advice, and who reviewed.
type ReviewResult =
    { Blocking: Finding list
      Advisory: Finding list
      Independence: Independence }

/// Proof that a task was reviewed: the advice that stays on the PR, findings a designer accepted
/// at the round limit, who reviewed, how many rounds it took.
type ReviewEvidence =
    { Advisory: Finding list
      Accepted: Finding list
      Independence: Independence
      Rounds: int
      Note: string option }

type ReviewTarget =
    | ToTestAuthor
    | ToImplementer

/// The new-side lines a unified diff shows: Visible = added + context lines of every hunk.
type ParsedDiff =
    { Added: Map<string, Set<int>>
      Visible: Map<string, Set<int>> }

[<RequireQualifiedAccess>]
module Diff =

    let private hunkHeader =
        System.Text.RegularExpressions.Regex(@"^@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")

    /// git bookings a path holding a booking, backslash, control or non-ASCII character as "..." with C escapes
    /// (octal bytes for UTF-8). Unquoted, it is the path a finding cites.
    let private unquote (path: string) =
        if path.Length < 2 || path.[0] <> '"' || path.[path.Length - 1] <> '"' then path
        else
            let body = path.Substring(1, path.Length - 2)
            let bytes = System.Collections.Generic.List<byte>()
            let mutable i = 0
            let isOctal (c: char) = c >= '0' && c <= '7'
            while i < body.Length do
                if body.[i] <> '\\' || i + 1 >= body.Length then
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(string body.[i]))
                    i <- i + 1
                elif isOctal body.[i + 1] then
                    let digits = body.Substring(i + 1) |> Seq.takeWhile isOctal |> Seq.truncate 3 |> Seq.toArray |> System.String
                    bytes.Add(byte (System.Convert.ToInt32(digits, 8) &&& 0xFF))
                    i <- i + 1 + digits.Length
                else
                    let c =
                        match body.[i + 1] with
                        | 'n' -> '\n' | 't' -> '\t' | 'r' -> '\r' | 'a' -> '\a' | 'b' -> '\b' | 'f' -> '\f' | 'v' -> '\v'
                        | other -> other
                    bytes.Add(byte c)
                    i <- i + 2
            System.Text.Encoding.UTF8.GetString(bytes.ToArray())

    /// Walks the new-side lines of a unified diff: `onAdded file line text` for each added line, `onContext file line` for each context line.
    let private walk (unified: string) (onAdded: string -> int -> string -> unit) (onContext: string -> int -> unit) : unit =
        let mutable file: string option = None
        let mutable newLine = 0
        let mutable oldLeft = 0
        let mutable newLeft = 0

        for line in unified.Replace("\r\n", "\n").Split('\n') do
            if oldLeft > 0 || newLeft > 0 then
                // inside a hunk the counts say how many lines remain, so '+++' text is content, not a header
                match (if line.Length > 0 then line.[0] else ' ') with
                | '+' ->
                    file |> Option.iter (fun f -> onAdded f newLine (line.Substring 1))
                    newLine <- newLine + 1
                    newLeft <- newLeft - 1
                | '-' -> oldLeft <- oldLeft - 1
                | '\\' -> ()
                | _ ->
                    file |> Option.iter (fun f -> onContext f newLine)
                    newLine <- newLine + 1
                    oldLeft <- oldLeft - 1
                    newLeft <- newLeft - 1
            elif line.StartsWith "+++ " then
                let path = unquote (line.Substring(4).Split('\t').[0])
                file <-if path = "/dev/null" then None elif path.StartsWith "b/" then Some(path.Substring 2) else Some path
            else
                let m = hunkHeader.Match line
                if m.Success then
                    let count (g: System.Text.RegularExpressions.Group) = if g.Success then int g.Value else 1
                    oldLeft <- count m.Groups.[1]
                    newLine <- int m.Groups.[2].Value
                    newLeft <- count m.Groups.[3]

    let parse (unified: string) : ParsedDiff =
        let mutable added = Map.empty<string, Set<int>>
        let mutable visible = Map.empty<string, Set<int>>
        let add (m: Map<string, Set<int>>) f n =
            m |> Map.add f (m |> Map.tryFind f |> Option.defaultValue Set.empty |> Set.add n)
        walk unified (fun f n _ -> added <- add added f n; visible <- add visible f n) (fun f n -> visible <- add visible f n)
        { Added = added; Visible = visible }

    /// The added lines with their text, per file (sorted by file, lines in order): what a check that reads the code, not just where it changed, needs.
    let addedLines (unified: string) : (string * (int * string) list) list =
        let mutable found = Map.empty<string, (int * string) list>
        walk unified (fun f n t -> found <- found |> Map.add f ((found |> Map.tryFind f |> Option.defaultValue []) @ [ n, t ])) (fun _ _ -> ())
        found |> Map.toList

[<RequireQualifiedAccess>]
module Review =

    let isBlocking (f: Finding) = f.Category <> Design

    /// Blocking findings first, advisory second.
    let split (findings: Finding list) : Finding list * Finding list = findings |> List.partition isBlocking

    /// Keeps the findings that cite a line the reviewer could see; the rest are dropped with the
    /// reason, never silently: a finding that cannot be located cannot be fixed.
    let validate (cites: string -> int -> bool) (findings: Finding list) : Finding list * (Finding * string) list =
        let kept, dropped = findings |> List.partition (fun f -> cites f.File f.Line)
        kept, dropped |> List.map (fun f -> f, $"{f.File}:{f.Line} is not in the diff")

    /// Where blocking findings go. Weak tests are the test author's; when both kinds are present
    /// the tests change first, because the implementation is judged against them.
    let route (blocking: Finding list) : ReviewTarget =
        if blocking |> List.exists (fun f -> f.Category = WeakTests) then ToTestAuthor else ToImplementer

    /// A re-review may add findings only on lines the fix changed; a finding that is the same
    /// category in the same file as one still open is that finding, not a new one.
    let reReviewAllows (open': Finding list) (changed: string -> int -> bool) : Finding -> bool =
        fun f ->
            changed f.File f.Line
            || open' |> List.exists (fun o -> o.File = f.File && o.Category = f.Category)
