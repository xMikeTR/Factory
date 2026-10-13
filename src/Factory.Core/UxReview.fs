namespace Factory.Core

open System
open System.Text.Json.Nodes

/// One freeze-time screenshot: an element rendered in one state at one breakpoint. The file is
/// `<element-slug>__<state>__<breakpoint>.png`, the name the repo's screenshot command must write.
type UxShot =
    { ElementSlug: string
      State: string
      Breakpoint: string
      Path: string }

/// The judge's reading of one screenshot against the design's stated intent. A gradient: nothing in this
/// type or its module can fail a run, and no gate reads it.
type UxScore =
    { Hierarchy: int
      Clarity: int
      Consistency: int
      Note: string }

[<RequireQualifiedAccess>]
module UxReview =
    /// Designc's element slug: the element's path lowercased, runs of non-alphanumerics as '-'.
    let slug (element: string) =
        let sb = Text.StringBuilder()
        for c in element.ToLowerInvariant() do
            if Char.IsLetterOrDigit c then sb.Append c |> ignore
            elif sb.Length > 0 && sb.[sb.Length - 1] <> '-' then sb.Append '-' |> ignore
        sb.ToString().Trim('-')

    /// `quotes-quote-ui__ready__mobile.png` -> shot; None for any other name.
    let parseShot (path: string) : UxShot option =
        let file = IO.Path.GetFileNameWithoutExtension path
        match file.Split([| "__" |], StringSplitOptions.None) with
        | [| el; st; bp |] when el <> "" && st <> "" && bp <> "" && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ->
            Some { ElementSlug = el; State = st; Breakpoint = bp; Path = path }
        | _ -> None

    /// The calibrated rubric the judge scores against: three 1-5 axes with anchors, so scores compare across regenerations.
    let rubric =
        """Score the screenshot against the design intent given. Three axes, each an integer 1 to 5:
- hierarchy: 1 = the primary action and the main content cannot be told apart from the rest; 3 = the main content is findable but competes; 5 = the eye lands on the primary content and action first.
- clarity: 1 = labels, states or next steps are unclear; 3 = understandable with effort; 5 = every control and message says what it does.
- consistency: 1 = spacing, type and controls disagree with each other or the stated design; 3 = mostly consistent; 5 = indistinguishable in style from the rest of the design.
Add one sentence in `note` naming the single biggest issue (or "none"). This is a gradient for tracking; it never blocks a merge."""

    /// The model's submit_ux_scores arguments: all three axes 1..5, or the reason it is not recorded.
    let parseScore (hierarchy: int) (clarity: int) (consistency: int) (note: string) : Result<UxScore, string> =
        let bad = [ "hierarchy", hierarchy; "clarity", clarity; "consistency", consistency ] |> List.filter (fun (_, v) -> v < 1 || v > 5)
        match bad with
        | [] -> Ok { Hierarchy = hierarchy; Clarity = clarity; Consistency = consistency; Note = (if isNull note then "" else note) }
        | _ -> Error(String.Join("; ", bad |> List.map (fun (n, v) -> $"{n} is {v}, must be 1 to 5")))

    let mean (s: UxScore) = float (s.Hierarchy + s.Clarity + s.Consistency) / 3.0

    /// Mean score per element over every shot, for tracking an element across regenerations.
    let perElement (scored: (UxShot * UxScore) list) : (string * float) list =
        scored
        |> List.groupBy (fun (shot, _) -> shot.ElementSlug)
        |> List.map (fun (el, xs) -> el, xs |> List.averageBy (snd >> mean))
        |> List.sortBy fst
