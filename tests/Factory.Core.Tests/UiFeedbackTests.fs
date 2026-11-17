module Factory.Core.Tests.UiFeedbackTests

open System.IO
open Factory.Core
open Factory.Core.Tests.Support

let private dir = Path.Combine(__SOURCE_DIRECTORY__, "..", "Factory.Integration.Tests", "Fixtures", "ui-feedback")
let private read name = File.ReadAllText(Path.Combine(dir, name))
let private failures screen name = UiReport.failures screen (read name)
let private forState name state =
    let fs = failures "bordereaux" (name + ".report.json") |> List.filter (fun f -> f.State = state)
    UiFeedback.forState "bordereaux" state fs (read (name + ".output.txt"))

let private canned = "\u001b[31m FAIL \u001b[39m ui-page (chromium)  ui-contract/tests/b.page.test.tsx:18 > b page > ready aria\nAssertionError: x\n\n- Expected\n+ Received\n\n- a\n+ b\n\n ❯ checkAria f.tsx:1:1\n\nTrace:\n  step  locator\n  react.render  page\n\n⎯⎯⎯⎯⎯⎯⎯[1/1]⎯\n"
let private fail state kind placement = { State = state; Kind = kind; Placement = placement; Detail = "d" }

let tests : (string * (unit -> unit)) list =
    [ "ui feedback: an ARIA failure on the real run gives the diff text, the timeline and no image",
      fun () ->
          let f = forState "aria" "ready"
          equal [] f.Images "no images when ARIA failed"
          check (f.Text.Contains "- - heading \"Bordereaux\" [level=1]") "expected line"
          check (f.Text.Contains "+ - heading \"Bordereaux list\" [level=1]") "received line"
          check (f.Text.Contains "react.render") "timeline"
          check (f.Text.Contains "toBeInTheDocument") "timeline steps"
          check (not (f.Text.Contains "Failure screenshot")) "no screenshot path in text"
          check (not (f.Text.Contains "\u001b")) "no ANSI"
          check (not (f.Text.Contains "checkAria ui-contract")) "no code frame"

      "ui feedback: ARIA failing wins even when a placement also differs in that state",
      fun () ->
          let ks = failures "bordereaux" "aria.report.json" |> List.filter (fun f -> f.State = "ready") |> List.map (fun f -> f.Kind) |> List.sort
          equal [ "aria"; "placement-pixels" ] ks "both kinds failed in the fixture"
          equal [] (forState "aria" "ready").Images "still no image"

      "ui feedback: a pixel-only failure on the real run says ARIA matches and gives the diff images",
      fun () ->
          let f = forState "pixels" "ready"
          check (f.Text.StartsWith "ARIA matches the spec; the look differs") "opening line"
          check (f.Text.Contains "premium") "names the placement"
          check (f.Text.Contains "802 pixels") "pixel count"
          check (f.Text.Contains "toMatchScreenshot [ERROR]") "timeline"
          equal [ ".vitest/attachments/ui-contract/tests/bordereaux.page.test.tsx/ui__bordereaux__ready__premium-diff-chromium-linux.png" ] f.Images "diff image, relative to the repo"

      "ui feedback: a state that passed has no feedback",
      fun () ->
          let f = forState "pixels" "loading"
          equal "" f.Text "empty"
          equal [] f.Images "none"

      "ui feedback: ARIA diff is cut at 4000 characters with an ellipsis",
      fun () ->
          let big = String.replicate 6000 "x"
          let out = canned.Replace("- a\n+ b", "- " + big)
          let f = UiFeedback.forState "b" "ready" [ fail "ready" "aria" None ] out
          check (f.Text.Contains(String.replicate 3900 "x")) "kept long"
          check (f.Text.Contains " …") "cut mark"
          check (not (f.Text.Contains big)) "not whole"

      "ui feedback: no block in the output falls back to the failure's own line and says there is no timeline",
      fun () ->
          let f = UiFeedback.forState "b" "ready" [ { fail "ready" "aria" None with Detail = "Error: tree differs" } ] ""
          check (f.Text.Contains "Error: tree differs") "detail"
          check (f.Text.Contains "no step timeline in the output") "marker"

      "ui feedback: placement-missing and code are text only",
      fun () ->
          let f = UiFeedback.forState "b" "ready" [ { fail "ready" "placement-missing" (Some "hdr") with Detail = "Placement 'hdr' has no element" } ] ""
          equal [] f.Images "none"
          check (f.Text.Contains "Placement 'hdr' has no element") "detail"
          let c = UiFeedback.forState "b" "ready" [ { fail "ready" "code" None with Detail = "rule: bad" } ] ""
          check (c.Text.Contains "rule: bad") "code detail"

      "ui feedback: blocks are matched on the screen's own describe, not another screen's",
      fun () ->
          let f = UiFeedback.forState "other" "ready" [ fail "ready" "aria" None ] canned
          check (f.Text.Contains "no step timeline in the output") "other screen's block is not used" ]
