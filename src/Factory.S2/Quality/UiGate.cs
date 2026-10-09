using System.Text.Json;
using Factory.Core;
using Factory.Harness.Policy;
using Factory.S2.Git;
using FsCore = Factory.Core.Core;

namespace Factory.S2.Quality;

/// The UI regression guard over a task's worktree: what the UI offered on the integration branch (before this task) and what
/// the human-owned contract lists, against what the worktree offers now. Names only (see UiInventory); no build, so it is cheap.
public static class UiGate
{
    public static async Task<IReadOnlyList<UiViolation>> CheckAsync(
        GitWorkspace git, string worktree, string runId, UiPolicy policy, IReadOnlyList<UiItem> contract, IReadOnlyList<UiItem> authorised, CancellationToken ct)
    {
        var globs = policy.Globs.Count > 0 ? policy.Globs : UiInventory.DefaultGlobs;
        var testGlobs = policy.TestGlobs.Count > 0 ? policy.TestGlobs : UiInventory.DefaultTestGlobs;
        await using var baseline = await git.BaselineCheckoutAsync(runId, ct);
        return UiGuard.check(FsCore.ToList(contract), FsCore.ToList(authorised),
            FsCore.ToList(UiInventory.Scan(baseline.Path, globs, testGlobs)), FsCore.ToList(UiInventory.Scan(worktree, globs, testGlobs))).ToList();
    }

    /// What a designer reads (and the agent is told to restore).
    public static string Line(UiViolation v) => v switch
    {
        UiViolation.Removed r => $"- {Describe(r.Item)} existed and is gone",
        UiViolation.NowSkipped s => $"- {Describe(s.Item)} is now skipped",
        UiViolation.ContractMissing c => $"- {Describe(c.Item)} is in .factory/ui-contract.yaml and is missing",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static UiItem ItemOf(UiViolation v) => v switch
    {
        UiViolation.Removed r => r.Item,
        UiViolation.NowSkipped s => s.Item,
        UiViolation.ContractMissing c => c.Item,
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string Describe(UiItem i) => i.Kind.Tag switch
    {
        var t when t == UiItemKind.Tags.TestId => $"data-testid \"{i.Id}\"",
        var t when t == UiItemKind.Tags.Route => $"route \"{i.Id}\"",
        _ => $"UI test \"{i.Id}\"",
    };

    /// The ledger payload of an accepted removal.
    public static string Encode(IEnumerable<UiItem> items, string? note) =>
        JsonSerializer.Serialize(new { items = items.Select(i => new { kind = i.Kind.ToString(), id = i.Id }), note });

    public static IReadOnlyList<UiItem> Decode(string payload) =>
        JsonDocument.Parse(payload).RootElement.GetProperty("items").EnumerateArray()
            .Select(e => new UiItem(e.GetProperty("kind").GetString() switch
            {
                "TestId" => UiItemKind.TestId,
                "Route" => UiItemKind.Route,
                "SkippedTest" => UiItemKind.SkippedTest,
                _ => UiItemKind.UiTest,
            }, e.GetProperty("id").GetString()!)).ToList();
}
