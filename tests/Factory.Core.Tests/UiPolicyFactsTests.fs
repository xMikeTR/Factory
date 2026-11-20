module Factory.Core.Tests.UiPolicyFactsTests

open Factory.Core
open Factory.Core.Tests.Support

/// Every row of the kit hook's sabotage table (.claude/hooks/ui-contract-guard.test.mjs), mapped to the Factory's tools:
/// Write/Edit -> write_file path, Bash/PowerShell -> run_command command, Read -> read_file path, Grep -> list_files directory.
/// A command row is DENY when the command updates snapshots or touches the contract; a path row when the path is protected / holdout.
type private Row = Path of string | Read of string | Holdout of string | Cmd of string

let private denied row =
    match row with
    | Path p -> UiPolicyFacts.isUiProtected p
    | Read p | Holdout p -> UiPolicyFacts.isHoldout p
    | Cmd c -> UiPolicyFacts.updatesSnapshots c || UiPolicyFacts.touchesUiContract c

let private deny =
    [ "edits an approved preview PNG", Path "ui-contract/previews/bordereaux/ready-chromium-linux.png"
      "edits an approved ARIA tree", Path "ui-contract/previews/bordereaux/ready.aria.yml"
      "edits an approved placement baseline", Path "ui-contract/previews/bordereaux/placements/empty__premium-chromium-linux.png"
      "reaches the preview through ..", Path "src/pages/../../ui-contract/previews/bordereaux/ready.aria.yml"
      "reaches the preview through ./src/../", Path "./src/../ui-contract/previews/bordereaux/ready.png"
      "reaches the preview by absolute path", Path "/work/app/ui-contract/previews/bordereaux/ready.aria.yml"
      "reaches the preview by a Windows absolute path", Path "C:\\work\\app\\ui-contract\\previews\\bordereaux\\ready.aria.yml"
      "uses Windows separators", Path "ui-contract\\previews\\bordereaux\\ready.aria.yml"
      "edits an approved spec", Path "ui-contract/specs/bordereaux.json"
      "hand-writes a draft screen's preview (no draft exception)", Path "ui-contract/previews/claims/ready.aria.yml"
      "writes a draft spec (flipped from ALLOW)", Path "ui-contract/specs/claims.json"
      "writes a new spec (flipped from ALLOW)", Path "ui-contract/specs/treaties.json"
      "loosens the ratio in the helper", Path "ui-contract/preview/vitest-ui.tsx"
      "edits a manifest", Path "ui-contract/manifests/DataTable.json"
      "edits a generated page test", Path "ui-contract/tests/bordereaux.page.test.tsx"
      "edits a generated scenario test", Path "ui-contract/tests/orders.scenarios.page.test.tsx"
      "edits the Vitest config", Path "vitest.config.ts"
      "edits this hook", Path ".claude/hooks/ui-contract-guard.mjs"
      "writes a holdout sample", Path "ui-contract/holdout/bordereaux.json"
      "writes ui/holdout", Path "ui/holdout/bordereaux.json"
      "sed -i on the helper", Cmd "sed -i 's/0.001/0.5/' ui-contract/preview/vitest-ui.tsx"
      "vitest -u with no project", Cmd "npx vitest run -u"
      "vitest -u on the page project", Cmd "npx vitest run --project ui-page -u"
      "vitest --update on the page project", Cmd "npx vitest run --project ui-page --update"
      "vitest update argument", Cmd "npx vitest run update"
      "preview update with no screen named", Cmd "npx vitest run --project ui-preview -u"
      "npm run ui:preview with no screen named", Cmd "npm run ui:preview"
      "re-renders an approved screen", Cmd "npx vitest run --project ui-preview -u bordereaux.preview"
      "hides an approved screen behind a draft one", Cmd "npx vitest run --project ui-preview -u claims.preview bordereaux.preview"
      "renders a draft screen's previews (flipped from ALLOW)", Cmd "npx vitest run --project ui-preview -u claims.preview"
      "renders through the npm script (flipped from ALLOW)", Cmd "npm run ui:preview -- claims.preview"
      "update on a second line", Cmd "echo ok\nnpx vitest run --project ui-page -u"
      "update on a CRLF second line", Cmd "echo ok\r\nnpx vitest run --project ui-page -u"
      "update after &&", Cmd "cd ui-contract && npx vitest run --project=ui-page -u"
      "update after ;", Cmd "npm test; npx vitest -u"
      "rm an approved preview", Cmd "rm ui-contract/previews/bordereaux/ready-chromium-linux.png"
      "cp over a preview", Cmd "cp /tmp/x.png ui-contract/previews/bordereaux/ready-chromium-linux.png"
      "redirect into a preview", Cmd "echo x > ui-contract/previews/bordereaux/ready.aria.yml"
      "redirect with no space", Cmd "echo x >ui-contract/previews/bordereaux/ready.aria.yml"
      "PowerShell Copy-Item over a preview", Cmd "Copy-Item x.png ui-contract\\previews\\bordereaux\\ready-chromium-win32.png"
      "git checkout of approved previews", Cmd "git checkout main -- ui-contract/previews"
      "rm from inside the folder", Cmd "cd ui-contract && rm previews/bordereaux/ready.aria.yml"
      "sed -i on the config", Cmd "sed -i 's/0.001/0.5/' vitest.config.ts"
      "rm the whole contract folder", Cmd "rm -rf ui-contract"
      "reads a holdout sample", Holdout "ui-contract/holdout/bordereaux.json"
      "reads a holdout sample through ..", Holdout "src/../ui-contract/holdout/bordereaux.json"
      "greps the holdout folder", Holdout "ui-contract/holdout"
      "lists ui/holdout", Holdout "ui/holdout/"
      "cats a holdout sample", Cmd "cat ui-contract/holdout/bordereaux.json"
      "cats it from inside the folder", Cmd "cd ui-contract && cat holdout/bordereaux.json"
      "cats ui/holdout", Cmd "cat ui/holdout/bordereaux.json" ]

let private allow =
    [ "runs the page tests", Cmd "npx vitest run --project ui-page"
      "runs the holdout tests", Cmd "npm run ui:holdout"
      "runs related tests", Cmd "npx vitest related src/pages/Claims.tsx --run"
      "reads an approved preview", Read "ui-contract/previews/bordereaux/ready.aria.yml"
      "reads an approved spec", Read "ui-contract/specs/bordereaux.json"
      "writes the page", Path "src/pages/Bordereaux.tsx"
      "writes a page test outside the generated folder", Path "ui-contract/ui-tests/claims.page.test.tsx"
      "writes src/ui/Widget.tsx", Path "src/ui/Widget.tsx"
      "lists the previews", Cmd "ls ui-contract/previews/bordereaux"
      "cats an approved preview's tree", Cmd "cat ui-contract/previews/bordereaux/ready.aria.yml"
      "an unrelated command with a -u flag", Cmd "git push -u origin feature/claims"
      "sort -u", Cmd "sort -u names.txt"
      "an ordinary build", Cmd "dotnet test" ]

let tests : (string * (unit -> unit)) list =
    [ for name, row in deny -> $"ui policy facts: denies {name}", fun () -> check (denied row) name
      for name, row in allow -> $"ui policy facts: allows {name}", fun () -> check (not (denied row)) name
      yield "ui policy facts: only a holdout path is a holdout", fun () ->
          check (not (UiPolicyFacts.isHoldout "ui-contract/previews/a.png")) "previews are not holdout"
          check (not (UiPolicyFacts.isHoldout "src/holdout.ts")) "a file named holdout elsewhere"
      yield "ui policy facts: normalise collapses .. and ./ and separators", fun () ->
          equal "ui-contract/previews/x.png" (UiPolicyFacts.normalise "src/pages/../../ui-contract/previews/x.png") "dotdot"
          equal "ui-contract/previews/x.png" (UiPolicyFacts.normalise "./ui-contract\\previews\\x.png") "separators"
          equal "x.png" (UiPolicyFacts.normalise "../../../x.png") "cannot climb above the virtual root"
      yield "ui policy facts: an absolute path keeps its tail after the last anchor", fun () ->
          equal "ui-contract/previews/x.png" (UiPolicyFacts.normalise "/ui/app/ui-contract/previews/x.png") "absolute"
          equal "ui/holdout/b.json" (UiPolicyFacts.normalise "/home/u/ui/holdout/b.json") "ui anchor" ]
