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

    /// The placement ids a spec asks the page to carry.
    let placementIds (specJson: string) : string list =
        use spec = JsonDocument.Parse specJson
        placements spec.RootElement |> List.map fst |> List.distinct

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
import { describe, test, expect, afterEach } from "vitest";
import { page, server } from "vitest/browser";
import { render } from "vitest-browser-react";
import spec from "../specs/@SCREEN@.json";
import { @COMPONENT@ as Page } from "../../@FILE@";
import { ariaFiles, checkAria, checkPixels, checkPlacements, handlersFor, useWorker } from "../preview/vitest-ui";

const approvedAria = ariaFiles(import.meta.glob("../previews/**/*.aria.yml", { query: "?raw", import: "default", eager: true }) as Record<string, string>);

type Logged = { method: string; path: string; matched: boolean; body: string | null; status: number | null };

describe("@SCREEN@ page", () => {
  const worker = useWorker();

  // Every request the page makes, matched or not, written to .factory/requests/@SCREEN@/<state>.json for the Factory's request check
  // (it judges the files, this test does not). The body is kept as text so the Factory can shape it; nothing here is committed or ledgered.
  const byId = new Map<string, Logged>();
  const byState: Record<string, Logged[]> = {};
  let current = "";
  let bodies: Promise<unknown>[] = [];
  const note = (matched: boolean) => ({ request, requestId }: any) => {
    const e: Logged = { method: request.method, path: new URL(request.url).pathname, matched, body: null, status: null };
    byId.set(requestId, e);
    (byState[current] ??= []).push(e);
    bodies.push(request.clone().text().then((t: string) => { e.body = t || null; }, () => {}));
  };
  worker.events.on("request:match", note(true));
  worker.events.on("request:unhandled", note(false));
  worker.events.on("response:mocked", ({ response, requestId }: any) => { const e = byId.get(requestId); if (e) e.status = response.status; });
  afterEach(async () => {
    await Promise.all(bodies);
    bodies = [];
    const seen = new Set<string>();
    const rows = (byState[current] ?? []).filter((e) => { const k = JSON.stringify(e); return !seen.has(k) && seen.add(k); });
    await server.commands.writeFile(`.factory/requests/@SCREEN@/${current}.json`, JSON.stringify(rows, null, 1));
  });

  const open = async (state: string) => {
    current = state;
    worker.use(...handlersFor(spec as any, state));
    await render(<div data-testid="ui-root"><Page /></div>);
    await expect.element(page.getByTestId("ui-root")).toBeInTheDocument();
  };
"""
    let private perState = """  test("@S@ aria", async () => { await open("@S@"); await checkAria(spec as any, "@S@", "../previews", approvedAria); });
  test("@S@ pixels", async () => { await open("@S@"); await checkPixels(spec as any, "@S@"); });
  test("@S@ placements", async () => { await open("@S@"); await checkPlacements(spec as any, "@S@"); });
"""

    let text (screen: string) (component': string) (file: string) (states: string list) : string =
        let noExt = Regex.Replace(file.Replace('\\', '/'), @"\.[A-Za-z0-9]+$", "")
        let head = header.Replace("@SCREEN@", screen).Replace("@COMPONENT@", component').Replace("@FILE@", noExt)
        head + String.Join("", states |> List.map (fun s -> perState.Replace("@S@", s))) + "});\n"

type UiFailure = { State: string; Kind: string; Placement: string option; Detail: string }

/// Who a failing screen is blamed on: the tasks that link the element named like the page component, or write the page's file.
/// No task at all is a gap in the plan, reported by the caller, never a reason to blame everyone.
[<RequireQualifiedAccess>]
module UiBlame =
    let tasks (elements: ElementSpec list) (tasks: PlannedTask list) (component': string) (file: string) : string option * TaskId list =
        let element = elements |> List.tryFind (fun e -> e.Name = component')
        let blamed =
            tasks
            |> List.filter (fun t ->
                (element |> Option.exists (fun e -> List.contains e.Id t.Elements))
                || t.Writes |> List.exists (fun w -> PathPattern.matches w file))
            |> List.map (fun t -> t.Id)
        element |> Option.map (fun e -> e.Id.Value), blamed

/// Reads a Vitest JSON report of the generated page test. A failed `<state> placements` test is
/// `placement-missing` when the kit says the element is absent, else `placement-pixels`.
[<RequireQualifiedAccess>]
module UiReport =
    /// (state, kind, status, message) of this screen's tests. A test under another screen's `describe` is not this screen's.
    let private results (screen: string) (reportJson: string) =
        let start = reportJson.IndexOf '{'
        use d = JsonDocument.Parse(if start > 0 then reportJson.Substring start else reportJson)
        let all =
            match d.RootElement.TryGetProperty "testResults" with
            | true, files when files.ValueKind = JsonValueKind.Array ->
                [ for f in files.EnumerateArray() do
                      match f.TryGetProperty "assertionResults" with
                      | true, a -> yield! a.EnumerateArray() |> Seq.map (fun r -> r.Clone())
                      | _ -> () ]
            | _ -> []
        [ for r in all do
              let status = match r.TryGetProperty "status" with | true, s -> s.GetString() | _ -> ""
              let title = match r.TryGetProperty "title" with | true, t -> t.GetString() | _ -> ""
              let ours =
                  match r.TryGetProperty "ancestorTitles" with
                  | true, a when a.ValueKind = JsonValueKind.Array && a.GetArrayLength() > 0 -> a[0].GetString() = screen + " page"
                  | _ -> true
              let i = title.LastIndexOf ' '
              if ours && i > 0 then
                  let msg =
                      match r.TryGetProperty "failureMessages" with
                      | true, m when m.ValueKind = JsonValueKind.Array -> String.Join("\n", [ for x in m.EnumerateArray() -> x.GetString() ])
                      | _ -> ""
                  yield title.Substring(0, i), title.Substring(i + 1), status, msg ]

    /// How many of this screen's tests ran to a verdict (passed or failed): fewer than 3 per state means some never ran.
    let executed (screen: string) (reportJson: string) : int =
        results screen reportJson |> List.filter (fun (_, _, st, _) -> st = "passed" || st = "failed") |> List.length

    let failures (screen: string) (reportJson: string) : UiFailure list =
        [ for (state, kind, status, msg) in results screen reportJson do
              if status = "failed" then
                  let missing = Regex.Match(msg, "Placement '([a-z0-9-]+)' has no element with data-ui-id")
                  let shot = Regex.Match(msg, $"ui__{Regex.Escape screen}__{Regex.Escape state}__([a-z0-9-]+)")
                  let kind, placement =
                      match kind with
                      | "placements" when missing.Success -> "placement-missing", Some missing.Groups[1].Value
                      | "placements" ->
                          // Vitest names the files it writes `<name>-actual-<browser>-<platform>.png`: the placement id is what comes before that.
                          let id = if shot.Success then Some(Regex.Replace(shot.Groups[1].Value, "(-(actual|expected|diff|reference))?-(chromium|firefox|webkit)-[a-z0-9]+$", "")) else None
                          "placement-pixels", id
                      | k -> k, None
                  yield { State = state; Kind = kind; Placement = placement; Detail = (msg.Split('\n').[0]).Trim() } ]

/// The holdout Vitest file the Factory writes into a SCRATCH copy only (`ui-contract/tests/<screen>.holdout.test.tsx`): the kit's
/// bordereaux.holdout.test.tsx, importing the app's own registry like the page test, with no props (the page defaults them).
/// The sample is read from `ui-contract/holdout/<screen>.json`, which also exists only in the scratch copy.
[<RequireQualifiedAccess>]
module UiHoldoutTest =
    let private template = """import spec from "../specs/@SCREEN@.json";
import { components, layouts } from "../registry";
import { @COMPONENT@ as Page } from "../../@FILE@";
import { holdoutTests, type Holdout } from "../preview/vitest-ui";

const manifests = Object.values(import.meta.glob("../manifests/*.json", { eager: true, import: "default" })) as any[];
// A missing file gives no holdout, and holdoutTests then fails every state as could_not_run.
const holdout = (Object.values(import.meta.glob("../holdout/@SCREEN@.json", { eager: true, import: "default" }))[0] ?? {}) as Holdout;

holdoutTests(spec as any, Page, { components, layouts, manifests, holdout });
"""

    let text (screen: string) (component': string) (file: string) : string =
        let noExt = Regex.Replace(file.Replace('\\', '/'), @"\.[A-Za-z0-9]+$", "")
        template.Replace("@SCREEN@", screen).Replace("@COMPONENT@", component').Replace("@FILE@", noExt)

type private HoldoutTestVerdict = HPass | HFail | HCannot | HMissing

/// What a holdout run said about one screen.
type HoldoutReading =
    /// Every state matched.
    | HoldoutHeld
    /// These states did not (in spec order). Never more than state names.
    | HoldoutFailedStates of string list
    /// The run says nothing about the page. The text is the Factory's own, never the runner's output.
    | HoldoutCouldNotRun of string

/// Reads a Vitest JSON report of the generated holdout test (describe `<screen> holdout`, one test per state, titled by the state).
/// Nothing the runner printed is ever returned: a failing report can carry the holdout's data, so only state names and fixed text leave.
[<RequireQualifiedAccess>]
module UiHoldoutReport =
    let private failedMessage (m: string) =
        let first = (m.Split('\n').[0]).Trim()
        first.StartsWith "holdout_failed:" || first.StartsWith "Error: holdout_failed:" || first.StartsWith "AssertionError: holdout_failed:"

    let private couldNotMessage (m: string) =
        let first = (m.Split('\n').[0]).Trim()
        first.Contains "could_not_run:"

    let read (screen: string) (states: string list) (reportJson: string) : HoldoutReading =
        let unreadable = HoldoutCouldNotRun "the holdout report could not be read"
        try
            let start = reportJson.IndexOf '{'
            if start < 0 then unreadable
            else
                use d = JsonDocument.Parse(reportJson.Substring start)
                let results =
                    match d.RootElement.TryGetProperty "testResults" with
                    | true, files when files.ValueKind = JsonValueKind.Array ->
                        [ for f in files.EnumerateArray() do
                              match f.TryGetProperty "assertionResults" with
                              | true, a when a.ValueKind = JsonValueKind.Array ->
                                  for (r: JsonElement) in a.EnumerateArray() do
                                      let s (name: string) = match r.TryGetProperty name with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() | _ -> ""
                                      let ours =
                                          match r.TryGetProperty "ancestorTitles" with
                                          | true, a when a.ValueKind = JsonValueKind.Array && a.GetArrayLength() > 0 -> a[0].GetString() = screen + " holdout"
                                          | _ -> true
                                      let msg =
                                          match r.TryGetProperty "failureMessages" with
                                          | true, m when m.ValueKind = JsonValueKind.Array && m.GetArrayLength() > 0 -> m[0].GetString()
                                          | _ -> ""
                                      if ours then yield s "title", s "status", msg
                              | _ -> () ]
                    | _ -> []
                let byState = results |> List.map (fun (t, st, m) -> t, (st, m)) |> Map.ofList
                let verdict state =
                    match Map.tryFind state byState with
                    | Some("passed", _) -> HPass
                    | Some("failed", m) when couldNotMessage m && not (failedMessage m) -> HCannot
                    | Some("failed", _) -> HFail
                    | _ -> HMissing
                let verdicts = states |> List.map (fun s -> s, verdict s)
                let failed = verdicts |> List.filter (fun (_, v) -> v = HFail) |> List.map fst
                if not failed.IsEmpty then HoldoutFailedStates failed
                elif verdicts |> List.exists (fun (_, v) -> v = HCannot) then
                    HoldoutCouldNotRun "the holdout sample could not be used (it is missing, names data the spec does not declare, or equals the visible sample)"
                elif states.IsEmpty || verdicts |> List.exists (fun (_, v) -> v = HMissing) then
                    HoldoutCouldNotRun $"only some of the {states.Length} holdout tests for '{screen}' ran"
                else HoldoutHeld
        with :? JsonException -> unreadable

    /// The tail of the screen's verify outcome: generated text only, safe for a repair brief and a ledger.
    let tail (screen: string) (reading: HoldoutReading) : string =
        match reading with
        | HoldoutHeld -> $"holdout:{screen} passed"
        | HoldoutFailedStates states -> String.Join("\n", states |> List.map (fun s -> $"holdout_failed: {screen}/{s}"))
        | HoldoutCouldNotRun reason -> "could_not_run: " + reason

/// The retry cap per holdout: the nth failure of one (screen, criterion?) on one work item goes to a person once n > cap.
[<RequireQualifiedAccess>]
module HoldoutCap =
    let exceeded (cap: int) (failures: int) : bool = failures > cap
