namespace Factory.Core

open System
open System.Text.Json
open System.Text.RegularExpressions

/// One way a page departs from its spec, by rule name. Detail names ids and tags, never sample values.
type UiCodeProblem = { Rule: string; Detail: string }

/// A port of the kit's tools/check-code.ts: static, regex-level, cheap. The page tests catch what this cannot.
[<RequireQualifiedAccess>]
module UiCodeCheck =

    let private props (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    let private str (e: JsonElement) (name: string) =
        match props e name with
        | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    /// placement id -> component, from every slot of the spec.
    let private placements (spec: JsonElement) =
        match props spec "slots" with
        | Some slots when slots.ValueKind = JsonValueKind.Object ->
            [ for slot in slots.EnumerateObject() do
                  if slot.Value.ValueKind = JsonValueKind.Array then
                      for p in slot.Value.EnumerateArray() do
                          match str p "id", str p "component" with
                          | Some id, Some c -> yield id, c
                          | _ -> () ]
        | _ -> []

    let check (specJson: string) (manifestJsons: string list) (code: string) : UiCodeProblem list =
        use spec = JsonDocument.Parse specJson
        let manifests =
            manifestJsons
            |> List.map (fun m ->
                use d = JsonDocument.Parse m
                str d.RootElement "name" |> Option.defaultValue "", str d.RootElement "import" |> Option.defaultValue "")
        let allowed = manifests |> List.map snd |> List.distinct
        let catalogued = manifests |> List.map fst |> Set.ofList
        let expected = placements spec.RootElement
        let problems = ResizeArray<UiCodeProblem>()
        let add rule detail = problems.Add { Rule = rule; Detail = detail }

        let found = Collections.Generic.Dictionary<string, string>()
        let tags = Regex.Matches(code, @"<([A-Za-z][A-Za-z0-9.]*)\b") |> Seq.toList
        for m in Regex.Matches(code, """data-ui-id=\{?["']([\w-]+)["']\}?""") do
            let tag = tags |> List.filter (fun t -> t.Index < m.Index) |> List.tryLast |> Option.map (fun t -> t.Groups[1].Value) |> Option.defaultValue "?"
            let id = m.Groups[1].Value
            if found.ContainsKey id then add "duplicate-id" $"duplicate data-ui-id '{id}'" else found[id] <- tag
        for (id, comp) in expected do
            match found.TryGetValue id with
            | true, tag when tag <> comp -> add "placement-component" $"'{id}' should be <{comp}>, code has <{tag}>"
            | true, _ -> ()
            | _ -> add "placement-missing" $"spec placement '{id}' ({comp}) has no element with data-ui-id=\"{id}\""
        let known = expected |> List.map fst |> Set.ofList
        for kv in found do
            if not (known.Contains kv.Key) then add "unspecified-id" $"<{kv.Value} data-ui-id=\"{kv.Key}\"> is not in the spec"

        let importedFrom =
            [ for m in Regex.Matches(code, """import\s+(?:type\s+)?\{([^}]+)\}\s+from\s+["']([^"']+)["']""") do
                  for n in m.Groups[1].Value.Split(',') do
                      let name = (Regex.Split(n.Trim(), @"\s+as\s+") |> Array.last).Trim()
                      if name <> "" then yield name, m.Groups[2].Value ]
            |> dict
        let used = Regex.Matches(code, @"<([A-Z][A-Za-z0-9]*)\b") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.distinct |> Seq.toList
        for tag in used do
            match importedFrom.TryGetValue tag with
            | true, md ->
                if catalogued.Contains tag && not (List.contains md allowed) then
                    let expectedMods = String.Join(" | ", allowed)
                    add "import" $"{tag} imported from '{md}', expected {expectedMods}"
                if not (catalogued.Contains tag) && List.contains md allowed then
                    add "uncatalogued" $"<{tag}> comes from the UI library but has no approved manifest"
            | _ -> ()
        let raws = Regex.Matches(code, @"<(button|table|input|select|h[1-6])\b") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.distinct |> Seq.toList
        if not raws.IsEmpty then add "raw-element" ("raw elements: <" + String.Join(">, <", raws) + ">")
        if Regex.IsMatch(code, @"style=\{\{") then add "inline-style" "inline style={{...}} found"
        let rawValues =
            Regex.Matches(code, """["'`](\d+(?:\.\d+)?px|#[0-9a-fA-F]{3,8}|rgba?\([^)]*\))["'`]""")
            |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.distinct |> Seq.toList
        if not rawValues.IsEmpty then add "raw-value" "raw design values in the page: use tokens"
        List.ofSeq problems

/// The screen's states in spec order.
[<RequireQualifiedAccess>]
module UiSpecStates =
    let ofSpec (specJson: string) : string list =
        use d = JsonDocument.Parse specJson
        match d.RootElement.TryGetProperty "states" with
        | true, s when s.ValueKind = JsonValueKind.Object -> [ for p in s.EnumerateObject() -> p.Name ]
        | _ -> []

/// The approved files the page is judged against must be byte-for-byte what was seeded.
[<RequireQualifiedAccess>]
module UiBaseline =
    let private guarded (path: string) = path.StartsWith "ui-contract/previews/"

    /// baseline and current map repo-relative path to sha256. Added files count only under previews:
    /// a new baseline PNG is a way to make a failing screenshot pass.
    let drift (baseline: Map<string, string>) (current: Map<string, string>) : string list =
        [ for KeyValue(p, h) in baseline do
              match Map.tryFind p current with
              | None -> yield $"missing: {p}"
              | Some h2 when h2 <> h -> yield $"modified: {p}"
              | _ -> ()
          for KeyValue(p, _) in current do
              if guarded p && not (baseline.ContainsKey p) then yield $"added: {p}" ]

/// The Vitest file the Factory writes into `ui-contract/tests/`: the kit's pageTests, unrolled so each
/// (state, kind) is its own literal-titled test. The report then says which kind failed in which state.
[<RequireQualifiedAccess>]
module UiPageTest =
    let private header = """import React from "react";
import { describe, test } from "vitest";
import { render } from "vitest-browser-react";
import spec from "../specs/@SCREEN@.json";
import { @COMPONENT@ as Page } from "../../@FILE@";
import { ariaFiles, checkAria, checkPixels, checkPlacements, handlersFor, useWorker } from "../preview/vitest-ui";

const approvedAria = ariaFiles(import.meta.glob("../previews/**/*.aria.yml", { query: "?raw", import: "default", eager: true }) as Record<string, string>);

describe("@SCREEN@ page", () => {
  const worker = useWorker();
  const open = (state: string) => {
    worker.use(...handlersFor(spec as any, state));
    render(<div data-testid="ui-root"><Page /></div>);
  };
"""
    let private perState = """  test("@S@ aria", async () => { open("@S@"); await checkAria(spec as any, "@S@", "../previews", approvedAria); });
  test("@S@ pixels", async () => { open("@S@"); await checkPixels(spec as any, "@S@"); });
  test("@S@ placements", async () => { open("@S@"); await checkPlacements(spec as any, "@S@"); });
"""

    let text (screen: string) (component': string) (file: string) (states: string list) : string =
        let noExt = Regex.Replace(file.Replace('\\', '/'), @"\.[A-Za-z0-9]+$", "")
        let head = header.Replace("@SCREEN@", screen).Replace("@COMPONENT@", component').Replace("@FILE@", noExt)
        head + String.Join("", states |> List.map (fun s -> perState.Replace("@S@", s))) + "});\n"

type UiFailure = { State: string; Kind: string; Placement: string option; Detail: string }

/// Reads a Vitest JSON report of the generated page test. A failed `<state> placements` test is
/// `placement-missing` when the kit says the element is absent, else `placement-pixels`.
[<RequireQualifiedAccess>]
module UiReport =
    let failures (screen: string) (reportJson: string) : UiFailure list =
        let start = reportJson.IndexOf '{'
        use d = JsonDocument.Parse(if start > 0 then reportJson.Substring start else reportJson)
        let results =
            match d.RootElement.TryGetProperty "testResults" with
            | true, files when files.ValueKind = JsonValueKind.Array -> [ for f in files.EnumerateArray() do match f.TryGetProperty "assertionResults" with | true, a -> yield! a.EnumerateArray() | _ -> () ]
            | _ -> []
        [ for r in results do
              let status = match r.TryGetProperty "status" with | true, s -> s.GetString() | _ -> ""
              let title = match r.TryGetProperty "title" with | true, t -> t.GetString() | _ -> ""
              let i = title.LastIndexOf ' '
              if status = "failed" && i > 0 then
                  let state, kind = title.Substring(0, i), title.Substring(i + 1)
                  let msg =
                      match r.TryGetProperty "failureMessages" with
                      | true, m when m.ValueKind = JsonValueKind.Array -> String.Join("\n", [ for x in m.EnumerateArray() -> x.GetString() ])
                      | _ -> ""
                  let missing = Regex.Match(msg, "Placement '([a-z0-9-]+)' has no element with data-ui-id")
                  let shot = Regex.Match(msg, $"ui__{Regex.Escape screen}__{Regex.Escape state}__([a-z0-9-]+)")
                  let kind, placement =
                      match kind with
                      | "placements" when missing.Success -> "placement-missing", Some missing.Groups[1].Value
                      | "placements" -> "placement-pixels", (if shot.Success then Some shot.Groups[1].Value else None)
                      | k -> k, None
                  let detail = (msg.Split('\n').[0]).Trim()
                  yield { State = state; Kind = kind; Placement = placement; Detail = detail } ]
