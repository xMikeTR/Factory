using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Microsoft.Extensions.AI;
using Microsoft.FSharp.Collections;

namespace Factory.S2.Quality;

/// What a UX review left: the shots (for one human to look at) and, when the judge ran, its gradient.
public sealed record UxReviewResult(
    IReadOnlyList<UxShot> Shots,
    IReadOnlyList<(UxShot Shot, UxScore Score)> Scores,
    IReadOnlyList<string> Unscored);

/// Freeze-time subjective UX (ui-acceptance-playwright.md 8.3): screenshots for one human review, and an optional LLM
/// judge that scores them against the design's stated intent. Nothing here can fail a run or a merge; the scores are
/// ledgered as a gradient for Langfuse and S4.
public static class UxReviewRunner
{
    public static async Task<UxReviewResult> RunAsync(
        FactoryServices svc, Handover handover, string runId, string command, string worktree, bool judge, CancellationToken ct)
    {
        var keep = Path.Combine(svc.Settings.RunDir(runId), "ux-screenshots");
        var (outcome, shots) = await svc.Verify.ScreenshotsAsync(runId, command, worktree, keep, ct);
        await svc.LogAsync(runId, "ux_review", LedgerKinds.UxScreenshots, outcome: outcome.ExitCode == 0 ? "taken" : "command_failed",
            payload: JsonSerializer.Serialize(new
            {
                command, exit = outcome.ExitCode,
                shots = shots.Select(s => new { element = s.ElementSlug, state = s.State, breakpoint = s.Breakpoint, path = Path.GetFileName(s.Path) }),
            }), ct: ct);
        if (!judge || shots.Count == 0) return new UxReviewResult(shots, [], []);

        var scores = new List<(UxShot, UxScore)>();
        var unscored = new List<string>();
        foreach (var shot in shots)
        {
            var signals = new TurnSignals();
            var tools = new ScopedWorkspace(worktree, [], [], [], signals).UxJudgeTools();
            var agent = svc.Agents.Create(new AgentScope(runId, Roles.UxJudge, "ux", shot.ElementSlug), ModelTier.Standard,
                UxReview.rubric, tools, new TurnBudget());
            var built = new DataContent(await File.ReadAllBytesAsync(shot.Path, ct), "image/png");
            var (approved, note) = await ApprovedAsync(handover, shot, worktree, ct);
            var intent = Intent(handover, shot) + (note is null ? "" : $"\n{note}\n");
            var message = new ChatMessage(ChatRole.User, approved is null
                ? [new TextContent(intent), built]
                : [new TextContent(intent), new TextContent("Approved design"), approved, new TextContent("Built page"), built]);
            var response = await agent.RunAsync(message, cancellationToken: ct);
            if (signals.UxScore is not { } score)
            {
                unscored.Add($"{shot.ElementSlug} {shot.State} {shot.Breakpoint}");
                continue;
            }
            scores.Add((shot, score));
            await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "ux_review", LedgerKinds.UxJudge, Element: shot.ElementSlug,
                Outcome: "gradient", Model: ModelTier.Standard.ToString(), TokensIn: response.Usage?.InputTokenCount, TokensOut: response.Usage?.OutputTokenCount,
                Payload: JsonSerializer.Serialize(new
                {
                    state = shot.State, breakpoint = shot.Breakpoint, hierarchy = score.Hierarchy, clarity = score.Clarity,
                    consistency = score.Consistency, note = score.Note,
                })), ct);
        }
        return new UxReviewResult(shots, scores, unscored);
    }

    /// The approved page picture for a shot of a UI screen's component element, `ui-contract/previews/<screen>/<state>-chromium-linux.png` in the worktree,
    /// when its sha256 is the one the handover froze. A shot of another element gets neither picture nor note; a missing or changed file gets a note
    /// and no picture (the review goes on with the built page alone). The breakpoint is not matched.
    private static async Task<(DataContent? Approved, string? Note)> ApprovedAsync(Handover handover, UxShot shot, string worktree, CancellationToken ct)
    {
        var screen = handover.Ui.FirstOrDefault(u => UxReview.slug(u.Component) == shot.ElementSlug);
        if (screen is null) return (null, null);
        var name = $"{shot.State}-{Factory.Harness.Registry.UiPreview.Browser}-{Factory.Harness.Registry.UiPreview.Platform}.png";
        var path = Path.Combine(worktree, "ui-contract", "previews", screen.Screen, name);
        var frozen = screen.Previews.Where(p => p.Item1 == name).Select(p => p.Item2).FirstOrDefault();
        if (!File.Exists(path)) return (null, $"Note: no approved picture for state `{shot.State}` of screen `{screen.Screen}` was found; judge the built page alone.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (frozen is null || !string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), frozen, StringComparison.OrdinalIgnoreCase))
            return (null, $"Note: the approved picture for state `{shot.State}` of screen `{screen.Screen}` is not the one the handover froze, so it is not shown; judge the built page alone.");
        return (new DataContent(bytes, "image/png"), null);
    }

    /// The design intent for one shot: the element, the state and breakpoint, and the frozen criteria about that element.
    private static string Intent(Handover handover, UxShot shot)
    {
        var sb = new StringBuilder($"Element `{shot.ElementSlug}`, state `{shot.State}`, breakpoint `{shot.Breakpoint}`.\n");
        var criteria = handover.AcceptanceCriteria.Where(c => UxReview.slug(c.Element) == shot.ElementSlug).ToList();
        if (criteria.Count == 0) sb.AppendLine("The design states no acceptance criteria for this element; judge general clarity and hierarchy.");
        foreach (var c in criteria) sb.AppendLine($"### Design intent {c.Id}").AppendLine(c.Text.TrimEnd());
        return sb.ToString();
    }

    public static string Render(UxReviewResult r)
    {
        var sb = new StringBuilder($"{r.Shots.Count} screenshot(s)\n");
        foreach (var s in r.Shots) sb.AppendLine($"  {Path.GetFileName(s.Path)}");
        if (r.Scores.Count > 0)
        {
            sb.AppendLine("Judge (gradient, never blocks):");
            foreach (var (el, mean) in UxReview.perElement(ListModule.OfSeq(r.Scores.Select(x => Tuple.Create(x.Shot, x.Score)))).Select(x => (x.Item1, x.Item2)))
                sb.AppendLine($"  {el}: {mean:0.0}/5");
            foreach (var (shot, score) in r.Scores.Where(x => x.Score.Note is { Length: > 0 } n && n != "none"))
                sb.AppendLine($"  {shot.ElementSlug} {shot.State} {shot.Breakpoint}: {score.Note}");
        }
        foreach (var u in r.Unscored) sb.AppendLine($"  not scored: {u}");
        return sb.ToString();
    }
}
