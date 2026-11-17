namespace Factory.Core

open System
open System.Text.RegularExpressions

/// What a failing state tells the agent that has to fix it: text, and picture paths (relative to the repo root, under `.vitest/`).
type UiStateFeedback = { Text: string; Images: string list }

/// Failure feedback for one state of a screen, from the failures `UiReport.failures` read and Vitest 5.1's terminal output (the
/// default reporter prints, per failing test, a block headed ` FAIL  <project>  <file>:<line> > <screen> page > <state> <kind>`:
/// the message with its diff, a code frame, then `Trace:` with one line per step). The JSON report's message is cut short and has no
/// trace, so the blocks come from the terminal output (recorded in Fixtures/ui-feedback).
/// Order: if the ARIA tree differs, its diff and the step timeline and NO picture (the tree is wrong, so the picture only distracts);
/// if only pixels differ, "ARIA matches", the timeline and the diff images; placement-missing and code are text only.
[<RequireQualifiedAccess>]
module UiFeedback =
    let private ansi = Regex("\u001b\\[[0-9;]*[A-Za-z]")
    let stripAnsi (s: string) = ansi.Replace(s, "")

    /// The ARIA diff stays whole up to this many characters.
    let AriaChars = 4000
    let private TimelineChars = 2000

    let private cut (limit: int) (s: string) = if s.Length > limit then s.Substring(0, limit) + " …" else s

    /// (title after the screen's describe, body lines) for each failing test of this screen in the terminal output.
    let private blocks (screen: string) (output: string) : (string * string list) list =
        let lines = (stripAnsi output).Replace("\r\n", "\n").Split('\n') |> List.ofArray
        let isHeader (l: string) = l.TrimStart().StartsWith "FAIL "
        let isEnd (l: string) = isHeader l || l.StartsWith "⎯"
        let rec go (ls: string list) acc =
            match ls with
            | [] -> List.rev acc
            | l :: rest when isHeader l ->
                let parts = l.Split([| " > " |], StringSplitOptions.None)
                let body = rest |> List.takeWhile (isEnd >> not)
                let acc' =
                    if parts.Length >= 3 && parts.[parts.Length - 2].Trim() = screen + " page" then (parts.[parts.Length - 1].Trim(), body) :: acc
                    else acc
                go (List.skip body.Length rest) acc'
            | _ :: rest -> go rest acc
        go lines []

    let private find (title: string) bs = bs |> List.tryFind (fun (t, _) -> t = title) |> Option.map snd

    /// The message up to the code frame, without the failure-screenshot path (an ARIA failure carries no picture).
    let private ariaDiff (body: string list) =
        let msg = body |> List.takeWhile (fun l -> not (l.StartsWith " ❯ ") && not (l.StartsWith "Caused by"))
        let rec drop ls =
            match ls with
            | (l: string) :: rest when l.StartsWith "Failure screenshot:" -> drop (rest |> List.skipWhile (fun x -> x.TrimStart().StartsWith "- " && x.Contains ".png"))
            | l :: rest -> l :: drop rest
            | [] -> []
        String.Join("\n", drop msg).Trim()

    let private timeline (body: string list) =
        match body |> List.tryFindIndex (fun l -> l.TrimEnd() = "Trace:") with
        | Some i -> Some(cut TimelineChars (String.Join("\n", List.skip (i + 1) body).Trim()))
        | None -> None

    let private noTimeline = "no step timeline in the output"

    let private pixelLine (body: string list) =
        body |> List.tryPick (fun l -> let m = Regex.Match(l, @"\d+ pixels \(ratio [0-9.]+\) differ") in if m.Success then Some m.Value else None)

    let private diffImage (body: string list) =
        match body |> List.tryFindIndex (fun l -> l.Trim() = "Diff image:") with
        | Some i when i + 1 < body.Length ->
            let p = body.[i + 1].Trim()
            let j = p.IndexOf "/.vitest/"
            Some(if j >= 0 then p.Substring(j + 1) else p)
        | _ -> None

    let forState (screen: string) (state: string) (failures: UiFailure list) (output: string) : UiStateFeedback =
        let mine = failures |> List.filter (fun f -> f.State = state)
        let bs = blocks screen output
        let timelineOf title =
            match find title bs |> Option.bind timeline with
            | Some t -> $"Steps ({title}):\n{t}"
            | None -> $"Steps ({title}): {noTimeline}"
        if mine.IsEmpty then { Text = ""; Images = [] }
        elif mine |> List.exists (fun f -> f.Kind = "aria") then
            let title = state + " aria"
            let diff =
                match find title bs with
                | Some b when ariaDiff b <> "" -> ariaDiff b
                | _ -> (mine |> List.find (fun f -> f.Kind = "aria")).Detail
            { Text = $"The ARIA tree of state `{state}` differs from the approved one:\n{cut AriaChars diff}\n\n{timelineOf title}"; Images = [] }
        else
            let pixel = mine |> List.filter (fun f -> f.Kind = "pixels" || f.Kind = "placement-pixels")
            let others = mine |> List.filter (fun f -> f.Kind <> "pixels" && f.Kind <> "placement-pixels")
            let sb = Text.StringBuilder()
            if not pixel.IsEmpty then sb.Append($"ARIA matches the spec; the look differs (state `{state}`).\n") |> ignore
            let images = ResizeArray<string>()
            for f in pixel do
                let title = state + (if f.Kind = "pixels" then " pixels" else " placements")
                let body = find title bs |> Option.defaultValue []
                let place = defaultArg f.Placement "?"
                let what = if f.Kind = "pixels" then "page picture" else $"placement `{place}`"
                let px = defaultArg (pixelLine body) "differs"
                sb.Append($"- {what}: {px}\n") |> ignore
                sb.Append(timelineOf title + "\n") |> ignore
                match diffImage body with
                | Some p when not (images.Contains p) -> images.Add p
                | _ -> ()
            for f in others do
                let place = defaultArg f.Placement "?"
                let what = if f.Kind = "placement-missing" then $"placement `{place}` is missing" else f.Kind
                sb.Append($"- {what}: {f.Detail}\n") |> ignore
            { Text = sb.ToString().TrimEnd(); Images = List.ofSeq images }
