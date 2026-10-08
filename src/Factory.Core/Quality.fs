namespace Factory.Core

/// One function as an adapter measured it. The same shape for every language, so the ratchet is pure.
/// `Coverage` is the line coverage of the function (0..1); None when no coverage data reached it.
type FunctionMetrics =
    { File: string
      Name: string
      Line: int
      Cyclomatic: int
      Lines: int
      Coverage: float option }

type Measure =
    | Cyclomatic
    | Crap
    | Length

/// Limits for new code. In the protected `.factory/policy.yaml`.
type QualityThresholds = { MaxCyclomatic: int; MaxCrap: float; MaxLines: int }

/// Lines of a file the task changed (from the diff).
type ChangedRange = { File: string; First: int; Last: int }

type QualityViolation =
    { Function: FunctionMetrics
      Measure: Measure
      Value: float
      Limit: float
      /// What the function measured on the baseline; None for a new function.
      Baseline: float option }

[<RequireQualifiedAccess>]
module Quality =

    /// Change Risk Anti-Patterns: complexity punished by missing coverage. c^2 (1-cov)^3 + c.
    let crap (f: FunctionMetrics) =
        let c = float f.Cyclomatic
        let uncovered = 1.0 - (defaultArg f.Coverage 0.0)
        c * c * uncovered * uncovered * uncovered + c

    let private value (m: Measure) (f: FunctionMetrics) =
        match m with
        | Cyclomatic -> float f.Cyclomatic
        | Crap -> crap f
        | Length -> float f.Lines

    let private limit (t: QualityThresholds) (m: Measure) =
        match m with
        | Cyclomatic -> float t.MaxCyclomatic
        | Crap -> t.MaxCrap
        | Length -> float t.MaxLines

    /// A function is identified by file and name: moving it down the file is not a change.
    let private key (f: FunctionMetrics) = f.File, f.Name

    let private touches (ranges: ChangedRange list) (f: FunctionMetrics) =
        ranges |> List.exists (fun r -> r.File = f.File && r.First <= f.Line + f.Lines - 1 && r.Last >= f.Line)

    /// The ratchet. New functions must meet every threshold. A function that already existed and the
    /// task touched must be no worse than it was (so old debt is never demanded, only never grown).
    /// Functions the task did not touch are not judged. CRAP is judged only where coverage data exists.
    let check (t: QualityThresholds) (baseline: FunctionMetrics list) (current: FunctionMetrics list) (changed: ChangedRange list)
        : QualityViolation list =
        let before = baseline |> List.map (fun f -> key f, f) |> Map.ofList
        [ for f in current do
              if touches changed f then
                  // CRAP needs coverage: without data for this function it is not judged (rather than assumed 0%)
                  for m in [ Cyclomatic; Crap; Length ] |> List.filter (fun m -> m <> Crap || f.Coverage.IsSome) do
                      let v = value m f
                      match Map.tryFind (key f) before with
                      | None when v > limit t m -> yield { Function = f; Measure = m; Value = v; Limit = limit t m; Baseline = None }
                      | Some old when v > value m old ->
                          yield { Function = f; Measure = m; Value = v; Limit = value m old; Baseline = Some(value m old) }
                      | _ -> () ]
