using Factory.Core;
using Factory.Core.Gate;
using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Harness.Policy;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.S2.Quality;
using Microsoft.FSharp.Collections;
using Xunit;
using Approval = Factory.Core.Gate.Approval;
using CheckResult = Factory.Core.Gate.CheckResult;

namespace Factory.Integration.Tests.Sabotage;

/// The gate attacked on purpose: each case is a way an agent could fake "done", and each must be blocked by the
/// real component that guards it. Run after any change to a gate, a guard or the policies. A case that stops being
/// blocked means the gate went soft, never that the test needs loosening.
public class SabotageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sabotage-").FullName;
    private readonly List<string> _dirs = [];

    private static readonly IReadOnlyList<PathPattern> Protected =
        [PathPatternModule.createOrFail(".factory/**"), PathPatternModule.createOrFail("tests/acceptance/**")];

    private ScopedWorkspace Agent(IReadOnlyDictionary<string, string>? references = null) =>
        new(_root, [PathPatternModule.createOrFail("**")], Protected.ToList(), [], new TurnSignals(), references: references);

    /// A team worktree whose integration branch already has `files`.
    private static async Task<(TeamHarness H, string Wt)> TeamWith(params (string Path, string Text)[] files)
    {
        var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(wt, path))!);
            await File.WriteAllTextAsync(Path.Combine(wt, path), text);
        }
        await h.Services.Git.CommitAllAsync(wt, "baseline", CancellationToken.None);
        await h.GitIn(h.Services.Git.IntegrationPath(TeamHarness.RunId), "merge", "--ff-only", Factory.S2.Git.GitWorkspace.TeamBranch(TeamHarness.RunId, "team-x"));
        return (h, wt);
    }

    [Fact]
    public void Editing_the_gate_policy_is_refused()
    {
        Assert.StartsWith("DENIED", Agent().WriteFile(".factory/policy.yaml", "quality: {mode: off}"));
        Assert.False(File.Exists(Path.Combine(_root, ".factory/policy.yaml")));
    }

    [Fact]
    public void Writing_a_frozen_acceptance_spec_is_refused()
    {
        Assert.StartsWith("DENIED", Agent().WriteFile("tests/acceptance/W-1.spec.ts", "test('ok', () => {})"));
    }

    [Fact]
    public void A_frozen_acceptance_spec_changed_by_any_other_route_is_drift()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tests/acceptance"));
        File.WriteAllText(Path.Combine(_root, "tests/acceptance/W-1.spec.ts"), "expect(total).toBe(3)");
        var frozen = AcceptanceFiles.Hashes(_root).Select(h => new AcceptanceSpec(h.Path, h.Hash)).ToList();
        File.WriteAllText(Path.Combine(_root, "tests/acceptance/W-1.spec.ts"), "expect(total).toBeDefined()");

        var drift = AcceptanceSpecs.drift(ListModule.OfSeq(frozen),
            ListModule.OfSeq(AcceptanceFiles.Hashes(_root).Select(h => Tuple.Create(h.Path, h.Hash))));

        Assert.Equal([SpecDrift.NewModifiedSpec("tests/acceptance/W-1.spec.ts")], drift);
    }

    [Fact]
    public async Task Skipping_a_ui_test_is_a_violation()
    {
        var (h, wt) = await TeamWith(("web/Export.test.tsx", "test(\"exports the quote\", () => { expect(1).toBe(1); });\n"));
        using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(wt, "web/Export.test.tsx"), "test.skip(\"exports the quote\", () => { expect(1).toBe(1); });\n");

        var v = await UiGate.CheckAsync(h.Services.Git, wt, TeamHarness.RunId, new UiPolicy([], []), [], [], CancellationToken.None);

        Assert.Equal([UiViolation.NewNowSkipped(new UiItem(UiItemKind.UiTest, "exports the quote"))], v);
    }

    [Fact]
    public async Task Removing_an_assertion_is_a_drop()
    {
        var (h, wt) = await TeamWith(("tests/Calc.Tests/CalcTests.cs", "Assert.Equal(3, Add(1, 2));\nAssert.Equal(0, Add(0, 0));\n"));
        using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(wt, "tests/Calc.Tests/CalcTests.cs"), "Assert.Equal(3, Add(1, 2));\n");

        Assert.Equal([new AssertionDrop("tests/Calc.Tests/CalcTests.cs", 2, 1)],
            await QualityGate.AssertionsAsync(h.Services.Git, wt, TeamHarness.RunId, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_test_file_is_a_drop_to_zero()
    {
        var (h, wt) = await TeamWith(("web/calc.test.ts", "expect(add(1, 2)).toBe(3);\n"));
        using var _ = h;
        File.Delete(Path.Combine(wt, "web/calc.test.ts"));

        Assert.Equal([new AssertionDrop("web/calc.test.ts", 1, 0)],
            await QualityGate.AssertionsAsync(h.Services.Git, wt, TeamHarness.RunId, CancellationToken.None));
    }

    [Fact]
    public void Writing_into_a_read_only_reference_is_refused()
    {
        var legacy = Directory.CreateTempSubdirectory("legacy-").FullName;
        _dirs.Add(legacy);
        File.WriteAllText(Path.Combine(legacy, "Calc.cs"), "original");
        var ws = Agent(new Dictionary<string, string> { ["legacy"] = legacy });

        // An absolute path is read as repo-relative (the leading / is dropped), so it lands inside the worktree, never in the reference.
        ws.WriteFile(Path.Combine(legacy, "Calc.cs"), "changed");
        Assert.StartsWith("DENIED", ws.WriteFile(Path.GetRelativePath(_root, Path.Combine(legacy, "Calc.cs")), "changed"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(legacy, "Calc.cs")));
    }

    [Fact]
    public void A_proof_without_an_approval_or_with_a_failed_check_cannot_be_made()
    {
        var passed = new CheckResult("acceptance", true, "", FSharpList<Uri>.Empty);
        var failed = new CheckResult("mutation", false, "score 40 < 80", FSharpList<Uri>.Empty);
        var required = ListModule.OfSeq<string>(["acceptance", "mutation"]);

        Assert.True(ProofModule.tryCreate(required, new ProofBundle("T-1", "abc", ListModule.OfSeq([passed, failed]),
            ListModule.OfSeq([new Approval("mike", "ci")]))).IsError);
        Assert.True(ProofModule.tryCreate(required, new ProofBundle("T-1", "abc",
            ListModule.OfSeq([passed, new CheckResult("mutation", true, "", FSharpList<Uri>.Empty)]), FSharpList<Approval>.Empty)).IsError);
    }

    [Fact]
    public async Task Ready_for_qa_with_a_proof_for_an_older_commit_is_denied()
    {
        var gate = new JiraTransitionGate(AcsToolPolicy.Load(AcsToolPolicy.DefaultDirectory), new MemoryLedger(), enforce: true);
        var old = ProofModule.tryCreate(FSharpList<string>.Empty, new ProofBundle("T-1", "old", FSharpList<CheckResult>.Empty,
            ListModule.OfSeq([new Approval("mike", "ci")]))).ResultValue;

        Assert.False((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "new", [old])).Allowed);
    }

    public void Dispose()
    {
        foreach (var d in _dirs.Append(_root))
            try { Directory.Delete(d, true); } catch (IOException) { }
    }
}
