using Factory.Core;
using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Harness.Policy;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Quality;
using Factory.Integration.Tests.Verify;
using Xunit;
using FsCore = Factory.Core.Core;

namespace Factory.Integration.Tests.Sabotage;

/// The UI contract attacked on purpose (docs/ui-contract-verify.md section 3). Every row runs the real component: both policy
/// layers, the ScopedWorkspace, the F2 drift check, the UI guard, the code check. A row that stops being blocked means a guard went soft.
/// The one row not run here: a wrong token on one placement is caught by the F2 Docker test (UiPageCheck, pixel check), with no new Docker run.
public sealed class UiSabotageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ui-sabotage-").FullName;
    private readonly string _state = Directory.CreateTempSubdirectory("ui-sabotage-state").FullName;

    private static readonly IToolPolicy[] Layers = [new RoleAllowListPolicy(), AcsToolPolicy.Load(AcsToolPolicy.DefaultDirectory)];

    private static readonly IReadOnlyList<PathPattern> Protected = S2Config.Default.ProtectedPaths.Select(PathPatternModule.createOrFail).ToList();

    private static Dictionary<string, object?> Args(string key, string value) => new() { [key] = value };

    // ---- the snapshot update, in every form, against both policies and the workspace ----

    public static TheoryData<string> Updates() =>
    [
        "npx vitest run -u",
        "npx vitest run --update",
        "npx vitest run --project ui-page -u",
        "npx vitest -u",
        "npm run ui:preview",
        "npm run ui:preview -- bordereaux.preview",
        "npx vitest run && npx vitest run -u",
        "npx vitest run ; npx vitest run -u",
        "npm test\nnpx vitest run -u",
        "npm test\r\nnpm run ui:preview",
        "NPX VITEST RUN -U",
    ];

    [Theory]
    [MemberData(nameof(Updates))]
    public void A_snapshot_update_is_denied_by_both_policies(string command)
    {
        foreach (var layer in Layers)
            foreach (var role in new[] { Roles.TestAuthor, Roles.Implementer, Roles.Qa })
                Assert.False(layer.Evaluate(role, "run_command", Args("command", command)).Allowed, $"{layer.GetType().Name} / {role}: {command}");
    }

    [Theory]
    [MemberData(nameof(Updates))]
    public async Task A_snapshot_update_never_reaches_the_sandbox(string command)
    {
        var sandbox = new RecordingSandbox();
        var ws = new ScopedWorkspace(_root, [], [], ["npx vitest", "npm run", "npm test"], new TurnSignals(), sandbox);
        Assert.StartsWith("DENIED", await ws.RunCommand(command));
        Assert.Empty(sandbox.Commands);
    }

    // ---- writing and deleting the contract's files, by path, by traversal and by Windows separators ----

    private static readonly string[] ContractFiles =
    [
        "ui-contract/previews/bordereaux/ready-chromium-linux.png",
        "ui-contract/previews/bordereaux/ready.aria.yml",
        "ui-contract/previews/bordereaux/placements/ready__export-chromium-linux.png",
        "ui-contract/specs/bordereaux.json",
        "ui-contract/manifests/DataTable.json",
        "ui-contract/preview/vitest-ui.tsx",
        "ui-contract/tests/bordereaux.page.test.tsx",
        "vitest.config.ts",
        ".claude/hooks/block-snapshot-update.sh",
    ];

    public static TheoryData<string> Spellings()
    {
        var d = new TheoryData<string>();
        foreach (var f in ContractFiles)
        {
            d.Add(f);
            d.Add("./" + f);
            d.Add("src/../" + f);
            d.Add("src/pages/../../" + f);
            if (f.Contains('/')) d.Add(f.Replace('/', '\\')); // a Windows spelling only when it differs (else a duplicate row)
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void Writing_or_deleting_a_contract_file_is_denied_by_both_policies(string path)
    {
        foreach (var layer in Layers)
            foreach (var role in new[] { Roles.TestAuthor, Roles.Implementer })
                foreach (var tool in new[] { "write_file", "delete_file" })
                    Assert.False(layer.Evaluate(role, tool, Args("path", path)).Allowed, $"{layer.GetType().Name} / {role} / {tool}: {path}");
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void Writing_or_deleting_a_contract_file_is_refused_by_the_workspace_on_the_real_protected_list(string path)
    {
        var ws = new ScopedWorkspace(_root, [PathPatternModule.createOrFail("**")], Protected.ToList(), [], new TurnSignals());
        var target = Path.Combine(_root, path.Replace('\\', '/'));
        Assert.StartsWith("DENIED", ws.WriteFile(path, "x"));
        Assert.StartsWith("DENIED", ws.DeleteFile(path));
        Assert.False(File.Exists(Path.GetFullPath(target)));
    }

    // ---- the holdout ----

    public static TheoryData<string> HoldoutPaths() =>
        ["ui-contract/holdout/bordereaux.json", "ui/holdout/bordereaux.json", "src/../ui-contract/holdout/bordereaux.json", "ui-contract\\holdout\\bordereaux.json"];

    [Theory]
    [MemberData(nameof(HoldoutPaths))]
    public void The_holdout_cannot_be_read_by_any_file_reading_role_or_by_the_workspace(string path)
    {
        foreach (var layer in Layers)
            foreach (var role in new[] { Roles.Planner, Roles.Reviewer, Roles.TestAuthor, Roles.Implementer, Roles.Qa })
                Assert.False(layer.Evaluate(role, "read_file", Args("path", path)).Allowed, $"{layer.GetType().Name} / {role}: {path}");
        var holdout = Path.Combine(_root, "ui-contract/holdout/bordereaux.json");
        Directory.CreateDirectory(Path.GetDirectoryName(holdout)!);
        File.WriteAllText(holdout, "secret");
        Assert.DoesNotContain("secret", new ScopedWorkspace(_root, [], [], [], new TurnSignals()).ReadFile(path));
    }

    [Fact]
    public void The_holdout_cannot_be_listed_and_never_appears_in_a_listing()
    {
        foreach (var layer in Layers)
            Assert.False(layer.Evaluate(Roles.Implementer, "list_files", Args("directory", "ui-contract/holdout")).Allowed);
        var holdout = Path.Combine(_root, "ui-contract/holdout/bordereaux.json");
        Directory.CreateDirectory(Path.GetDirectoryName(holdout)!);
        File.WriteAllText(holdout, "secret");
        Assert.DoesNotContain("holdout", new ScopedWorkspace(_root, [], [], [], new TurnSignals()).ListFiles("."));
    }

    // ---- sabotage that gets past the agent's tools: found at the next gate ----

    private sealed record Seeded(GitWorkspace Git, string Integration, string RunDir, Handover Handover);

    private async Task<Seeded> SeedAsync()
    {
        var fx = await UiPreviewFixture.CreateAsync();
        var h = UiHandoverDir.Create();
        var json = await HandoverInput.ResolveAsync(h.Dir, _state, default);
        var handover = FsCore.ParseHandover(await File.ReadAllTextAsync(json));
        var runDir = Path.Combine(_state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(_state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        return new(git, integration, runDir, handover);
    }

    [Fact]
    public async Task A_raised_pixel_tolerance_in_the_preview_helper_or_config_is_drift()
    {
        var s = await SeedAsync();
        var helper = Path.Combine(s.Integration, "ui-contract", "preview", "vitest-ui.tsx");
        File.AppendAllText(helper, "\nexport const allowedMismatchedPixelRatio = 1;\n");
        File.AppendAllText(Path.Combine(s.Integration, "vitest.config.ts"), "\n// allowedMismatchedPixelRatio: 1\n");

        var drift = await UiContractSeed.DriftAsync(s.Integration, s.RunDir, default);

        Assert.Contains(drift, l => l.StartsWith("modified") && l.Contains("preview/vitest-ui.tsx"));
        Assert.Contains(drift, l => l.StartsWith("modified") && l.Contains("vitest.config.ts"));
    }

    [Fact]
    public async Task A_generated_state_test_that_is_skipped_is_drift_and_a_now_skipped_violation()
    {
        var s = await SeedAsync();
        var test = Path.Combine(s.Integration, "ui-contract", "tests", "bordereaux.page.test.tsx");
        var original = await File.ReadAllTextAsync(test);
        await File.WriteAllTextAsync(test, original.Replace("test(\"ready aria\"", "test.skip(\"ready aria\""));

        Assert.Contains(await UiContractSeed.DriftAsync(s.Integration, s.RunDir, default), l => l.StartsWith("modified") && l.Contains("bordereaux.page.test.tsx"));

        var git = s.Git;
        var violations = await UiGate.CheckAsync(git, s.Integration, "r1", new UiPolicy([], [PathPatternModule.createOrFail("**/*.page.test.tsx")]), [], [], default);
        Assert.Contains(violations, v => v is { IsNowSkipped: true } && UiGate.ItemOf(v).Id == "ready aria");
    }

    // ---- placements: a dropped data-ui-id, and the page code rules ----

    private const string Spec =
        """{"screen":"b","component":"BordereauxList","slots":{"main":[{"id":"table","component":"DataTable"},{"id":"export","component":"Button"}]}}""";
    private static readonly string[] Manifests = ["""{"name":"DataTable","import":"@your-org/ui"}""", """{"name":"Button","import":"@your-org/ui"}"""];

    [Fact]
    public void A_dropped_data_ui_id_is_a_code_check_problem()
    {
        var code = "import { DataTable, Button } from \"@your-org/ui\";\nexport const P = () => <div><DataTable data-ui-id=\"table\" /><Button /></div>;";
        var problems = UiCodeCheck.check(Spec, FsCore.ToList(Manifests), code);
        Assert.Contains(problems, p => p.Rule == "placement-missing" && p.Detail.Contains("export"));
    }

    [Fact]
    public void An_inline_style_is_a_code_check_problem()
    {
        var code = "import { DataTable, Button } from \"@your-org/ui\";\nexport const P = () => <div style={{ color: 'red' }}><DataTable data-ui-id=\"table\" /><Button data-ui-id=\"export\" /></div>;";
        Assert.Contains(UiCodeCheck.check(Spec, FsCore.ToList(Manifests), code), p => p.Rule == "inline-style");
    }

    [Fact]
    public async Task A_dropped_data_ui_id_is_a_contract_violation_for_the_guard()
    {
        var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        using var _ = h;
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(wt, "web"));
        await File.WriteAllTextAsync(Path.Combine(wt, "web/Page.tsx"), "<Table data-ui-id=\"table\" />\n");
        var item = new UiItem(UiItemKind.PlacementId, "table");

        var v = await UiGate.CheckAsync(h.Services.Git, wt, TeamHarness.RunId, new UiPolicy([], []), [item], [], CancellationToken.None);
        Assert.Empty(v);

        await File.WriteAllTextAsync(Path.Combine(wt, "web/Page.tsx"), "<Table />\n");
        v = await UiGate.CheckAsync(h.Services.Git, wt, TeamHarness.RunId, new UiPolicy([], []), [item], [], CancellationToken.None);
        Assert.Equal([UiViolation.NewContractMissing(item)], v);
    }

    private sealed class RecordingSandbox : Factory.Harness.Sandbox.ICommandSandbox
    {
        public List<string> Commands { get; } = [];
        public string Kind => "fake";
        public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new ProcessResult(0, "", TimeSpan.Zero, false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _state })
            try { Directory.Delete(d, true); } catch (IOException) { }
    }
}
