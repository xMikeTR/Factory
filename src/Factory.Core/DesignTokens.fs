namespace Factory.Core

open System
open System.Text.Json.Nodes

/// A named, typed design value. There are no anonymous values: a token without a type or a value is rejected.
type DesignToken =
    { Name: string
      Type: string
      Value: string }

[<RequireQualifiedAccess>]
module DesignTokens =
    /// Smallest body text the design may define, in CSS px. Checked on the token file, not in a browser.
    let minimumTextPx = 16.0

    /// W3C Design Tokens: nested groups, a leaf has `$value`, `$type` is inherited from the nearest group.
    /// The CSS name is the path joined with '-', so `font.size.body` is `--font-size-body`.
    let parse (json: string) : Result<DesignToken list, string list> =
        let errors = ResizeArray<string>()
        let tokens = ResizeArray<DesignToken>()

        let rec walk (path: string list) (inherited: string option) (node: JsonNode) =
            match node with
            | :? JsonObject as o ->
                let here = match o.["$type"] with | null -> inherited | t -> Some(t.GetValue<string>())
                if not (isNull o.["$value"]) then
                    let name = String.Join("-", List.rev path)
                    match here, o.["$value"] with
                    | None, _ -> errors.Add $"{name}: no $type"
                    | Some t, (:? JsonValue as v) -> tokens.Add({ Name = name; Type = t; Value = v.ToString() })
                    | Some _, _ -> errors.Add $"{name}: $value must be a single value"
                else
                    for kv in o do
                        if not (kv.Key.StartsWith "$") && not (isNull kv.Value) then walk (kv.Key :: path) here kv.Value
            | _ -> errors.Add(String.Join("-", List.rev path) + ": expected a token or a group")

        try
            match JsonNode.Parse json with
            | :? JsonObject as root -> walk [] None root
            | _ -> errors.Add "tokens.json: expected an object"
        with :? System.Text.Json.JsonException as e -> errors.Add $"tokens.json: {e.Message}"

        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            match tokens |> Seq.groupBy (fun t -> t.Name) |> Seq.tryFind (fun (_, g) -> Seq.length g > 1) with
            | Some(n, _) -> Error [ $"{n}: defined twice" ]
            | None -> Ok(List.ofSeq tokens)

    /// Generated CSS custom properties, one per token, in token order.
    let toCss (tokens: DesignToken list) =
        let body = tokens |> List.map (fun t -> $"  --{t.Name}: {t.Value};") |> String.concat "\n"
        $":root {{\n{body}\n}}\n"

    let private toPx (value: string) =
        let v = value.Trim()
        let num (suffix: string) = Double.TryParse(v.Substring(0, v.Length - suffix.Length), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture)
        if v.EndsWith "px" then (match num "px" with | true, n -> Some n | _ -> None)
        elif v.EndsWith "rem" then (match num "rem" with | true, n -> Some(n * 16.0) | _ -> None)
        else None

    /// Text-size tokens (`font-size-*`) below the minimum. The S1 lint, so a design cannot freeze them.
    let belowMinimumText (tokens: DesignToken list) : DesignToken list =
        tokens
        |> List.filter (fun t ->
            t.Name.StartsWith "font-size" && (toPx t.Value |> Option.exists (fun px -> px < minimumTextPx)))

/// One raw value the stylelint token gate rejected.
type LintViolation =
    { File: string
      Line: int
      Rule: string
      Text: string }

[<RequireQualifiedAccess>]
module TokenLint =
    /// A violation is a TranslationError on every task whose `writes` cover the file. Files no task owns
    /// are returned apart: a gap in the plan, not something to blame on a task.
    let translationErrors (tasks: PlannedTask list) (violations: LintViolation list) : TranslationError list * string list =
        let owners (file: string) =
            tasks |> List.filter (fun t -> t.Writes |> List.exists (fun w -> PathPattern.matches w file))
        let attributed =
            violations
            |> List.collect (fun v -> owners v.File |> List.map (fun t -> TranslationError t.Id))
            |> List.distinct
        let orphans =
            violations |> List.map (fun v -> v.File) |> List.distinct |> List.filter (fun f -> List.isEmpty (owners f))
        attributed, orphans
