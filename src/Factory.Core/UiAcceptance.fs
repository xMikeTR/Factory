namespace Factory.Core

open System

/// How the acceptance specs in a worktree differ from the ones frozen into the handover.
type SpecDrift =
    | MissingSpec of path: string
    | ModifiedSpec of path: string
    | AddedSpec of path: string

/// A failing acceptance spec, resolved to the AC and the design element it belongs to.
type SpecFailure =
    { Spec: string
      Ac: string
      Element: ElementSpec option }

/// Where a failing spec points: the tasks whose element it exercises.
type TranslationError = TranslationError of TaskId

[<RequireQualifiedAccess>]
module AcceptanceSpecs =
    let root = "tests/acceptance/"

    let isAcceptancePath (path: string) = path.Replace('\\', '/').StartsWith(root, StringComparison.Ordinal)

    /// Frozen specs against `actual` (path, hash) pairs read from tests/acceptance/** in the worktree.
    /// Anything but an exact match is a hard fail: the coding agent never owns these files.
    let drift (frozen: AcceptanceSpec list) (actual: (string * string) list) : SpecDrift list =
        let now = Map.ofList actual
        let was = frozen |> List.map (fun s -> s.Path, s.Hash) |> Map.ofList
        [ for s in frozen do
              match Map.tryFind s.Path now with
              | None -> yield MissingSpec s.Path
              | Some h when h <> s.Hash -> yield ModifiedSpec s.Path
              | Some _ -> ()
          for (p, _) in actual do
              if not (Map.containsKey p was) then yield AddedSpec p ]

    let describe =
        function
        | MissingSpec p -> $"{p} is frozen in the handover but missing from the worktree"
        | ModifiedSpec p -> $"{p} differs from its frozen hash"
        | AddedSpec p -> $"{p} is not in the handover's frozen acceptance specs"

    /// Designc's directory slug for an element label.
    let slug (s: string) =
        let chars = s.ToLowerInvariant() |> Seq.map (fun c -> if Char.IsAsciiLetterOrDigit c then c else '-') |> Array.ofSeq
        let t = (String chars).Trim('-')
        let collapsed = System.Text.RegularExpressions.Regex.Replace(t, "-+", "-")
        if collapsed = "" then "element" else collapsed

    /// `tests/acceptance/<element-slug>/<AC-id>.spec.ts` -> (slug, AC id).
    let parse (path: string) : (string * string) option =
        let p = path.Replace('\\', '/')
        if not (isAcceptancePath p) then None
        else
            match p.Substring(root.Length).Split('/') with
            | [| dir; file |] when file.EndsWith(".spec.ts", StringComparison.Ordinal) ->
                Some(dir, file.Substring(0, file.Length - ".spec.ts".Length))
            | _ -> None

    /// Resolve a failing spec to its element: the directory is the slug of the element's name or id.
    let failure (elements: ElementSpec list) (specPath: string) : SpecFailure option =
        parse specPath
        |> Option.map (fun (dir, ac) ->
            { Spec = specPath
              Ac = ac
              Element = elements |> List.tryFind (fun e -> slug e.Name = dir || slug e.Id.Value = dir) })

    /// A failing spec is a TranslationError on every task that changes its element, never the whole wave.
    /// An unresolved element, or one no task links, gives none: that is a gap in the plan or the binding,
    /// reported by the caller rather than blamed on a task.
    let translationErrors (tasks: PlannedTask list) (f: SpecFailure) : TranslationError list =
        match f.Element with
        | None -> []
        | Some el -> tasks |> List.filter (fun t -> List.contains el.Id t.Elements) |> List.map (fun t -> TranslationError t.Id)

// ---------- bug intake (Toyota flow) ----------

type BugReport = { Id: string; Summary: string }

/// The failing spec that reproduces a bug. The only way to get one is `FailingCheck.create`.
type FailingCheck =
    private
    | FailingCheck of spec: string

    member this.Spec = match this with FailingCheck s -> s

[<RequireQualifiedAccess>]
module FailingCheck =
    /// A reproduction must be an acceptance spec under tests/acceptance/**.
    let create (spec: string) : Result<FailingCheck, string> =
        if AcceptanceSpecs.isAcceptancePath spec && spec.EndsWith(".spec.ts", StringComparison.Ordinal) then Ok(FailingCheck spec)
        else Error $"A reproduction is a Playwright spec under {AcceptanceSpecs.root}; '{spec}' is not."

/// Where a defect came from, which decides what is fixed first (designc intake: SpecGap goes to S1).
type DefectOrigin =
    | SpecGap of requirement: string
    | DesignError of DecisionId
    | TranslationOrigin of TaskId
    | GateGap of gate: string

/// The countermeasure: a new or strengthened gate. A bug is not closed without one.
type NewGate = { Gate: string; Strengthens: string option }

type Defect =
    private
    | Reported of BugReport
    | Reproduced of BugReport * FailingCheck
    | Classified of FailingCheck * DefectOrigin
    | Countermeasured of DefectOrigin * NewGate

[<RequireQualifiedAccess>]
module Defect =
    let report (b: BugReport) = Reported b

    /// Reproduce first: a classification can only be made from a failing check.
    let reproduce (check: FailingCheck) (d: Defect) : Result<Defect, string> =
        match d with
        | Reported b -> Ok(Reproduced(b, check))
        | _ -> Error "Only a reported bug can be reproduced."

    let classify (origin: DefectOrigin) (d: Defect) : Result<Defect, string> =
        match d with
        | Reproduced(_, check) -> Ok(Classified(check, origin))
        | Reported _ -> Error "Reproduce the bug as a failing spec before classifying it."
        | _ -> Error "Only a reproduced bug can be classified."

    /// Closing needs a NewGate, so a fix without a countermeasure cannot be written.
    let close (gate: NewGate) (d: Defect) : Result<Defect, string> =
        match d with
        | Classified(_, origin) -> Ok(Countermeasured(origin, gate))
        | _ -> Error "Classify the bug before closing it."

    let isClosed =
        function
        | Countermeasured _ -> true
        | _ -> false

    /// SpecGap is fixed at S1, not by a code change: the design needs a criterion first.
    let originOf =
        function
        | Classified(_, o) | Countermeasured(o, _) -> Some o
        | _ -> None
