namespace Factory.Core

type MutantStatus =
    | Killed
    | Survived
    | Timeout
    | NoCoverage
    | CompileError
    | Ignored

/// One mutant as the mutation tool reported it. The same shape for every language.
type Mutant =
    { Id: string
      File: string
      Line: int
      Mutator: string
      Status: MutantStatus
      /// Names of the tests that ran this mutant, as the tool reported them (empty when none, or when it did not say).
      CoveredBy: string list
      /// Names of the tests that failed on it.
      KilledBy: string list }

/// Minimum mutation score on the lines a task changed. In the protected `.factory/policy.yaml`.
type MutationThresholds = { MinScore: float }

/// A file whose mutants on the changed lines scored below what was required.
type MutationViolation =
    { File: string
      Score: float
      Required: float
      Survivors: Mutant list }

/// One file's mutants on the changed lines: what it scored, what it was held to, and the counts by status.
type FileMutation =
    { File: string
      Score: float option
      Required: float
      Baseline: float option
      Killed: int
      Survived: int
      NoCoverage: int
      Timeout: int }

/// The evidence a mutation row carries: the score over every mutant on a changed line, and per file.
type MutationSummary = { Score: float option; Files: FileMutation list }

[<RequireQualifiedAccess>]
module Mutation =

    /// Killed and timed-out mutants are detected; compile errors and ignored ones say nothing about
    /// the tests. None when nothing is left to judge.
    let score (mutants: Mutant list) : float option =
        let judged = mutants |> List.filter (fun m -> m.Status <> CompileError && m.Status <> Ignored)
        match judged with
        | [] -> None
        | _ ->
            let detected = judged |> List.filter (fun m -> m.Status = Killed || m.Status = Timeout) |> List.length
            Some(float detected / float judged.Length)

    /// Declared by the TestAuthor, excluded only once the reviewer confirmed it.
    let confirmedEquivalents (declared: Set<string>) (confirmed: Set<string>) : Set<string> = Set.intersect declared confirmed

    let private onChanged (ranges: ChangedRange list) (m: Mutant) =
        ranges |> List.exists (fun r -> r.File = m.File && r.First <= m.Line && m.Line <= r.Last)

    /// The gate over the changed lines, per file: each file needs the threshold, or, when it
    /// already scored lower on the baseline, no less than that. Empty means pass.
    let check (t: MutationThresholds) (baseline: Map<string, float>) (equivalents: Set<string>) (ranges: ChangedRange list) (mutants: Mutant list) : MutationViolation list =
        mutants
        |> List.filter (onChanged ranges)
        |> List.filter (fun m -> not (equivalents.Contains m.Id))
        |> List.groupBy (fun m -> m.File)
        |> List.choose (fun (file, ms) ->
            match score ms with
            | None -> None
            | Some s ->
                let required = match baseline.TryFind file with Some b -> min t.MinScore b | None -> t.MinScore
                if s >= required then None
                else
                    let survivors = ms |> List.filter (fun m -> m.Status = Survived || m.Status = NoCoverage)
                    Some { File = file; Score = s; Required = required; Survivors = survivors })

    /// What a survivor line says about the tests: who ran the mutant, that none reaches it, or that the tool did not say.
    let testsThatRan (m: Mutant) : string =
        match m.CoveredBy with
        | [] when m.Status = NoCoverage -> "no test reaches this line"
        | [] -> "the tool did not say which tests ran it"
        | names -> "tests that ran it: " + String.concat ", " names

    /// The same mutants `check` judges (changed lines, confirmed equivalents left out), as a score and per-file counts.
    let summary (t: MutationThresholds) (baseline: Map<string, float>) (equivalents: Set<string>) (ranges: ChangedRange list) (mutants: Mutant list) : MutationSummary =
        let judged = mutants |> List.filter (onChanged ranges) |> List.filter (fun m -> not (equivalents.Contains m.Id))
        let count st (ms: Mutant list) = ms |> List.filter (fun m -> m.Status = st) |> List.length
        let files =
            judged
            |> List.groupBy (fun m -> m.File)
            |> List.map (fun (file, ms) ->
                let b = baseline.TryFind file
                { File = file
                  Score = score ms
                  Required = (match b with Some v -> min t.MinScore v | None -> t.MinScore)
                  Baseline = b
                  Killed = count Killed ms
                  Survived = count Survived ms
                  NoCoverage = count NoCoverage ms
                  Timeout = count Timeout ms })
        { Score = score judged; Files = files }
