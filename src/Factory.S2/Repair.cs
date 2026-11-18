using System.Text;
using System.Text.Json;
using Factory.Harness;

namespace Factory.S2;

/// repair.json: why a repair run exists (an integration that failed frozen specs or token lint, or a CI failure reported with `ci-report`). A run that ended `integration_failed` on frozen-spec or
/// token-lint failures is repaired by a new run cut from the failed integration branch that re-runs
/// only the tasks those failures blame; every other task is carried (its work is already on base).
/// Feedback maps a task id to what its brief must say. Images maps a task id to pictures (paths under the run dir, `evidence/ui/...`) that
/// go with it; null in a repair.json written before pictures existed.
public sealed record RepairFile(string Of, IReadOnlyDictionary<string, string> Feedback, IReadOnlyDictionary<string, string[]>? Images = null);

public static class Repair
{
    private const int TraceChars = 2000;
    /// A brief carries at most this many pictures.
    public const int MaxImages = 4;

    /// The blamed tasks and what failed for each, read from the failed run's ledger; null when
    /// the run recorded no failure that names a task (nothing to repair).
    public static async Task<RepairFile?> FromFailedRunAsync(FactoryServices svc, string failedRunId, CancellationToken ct)
    {
        var lines = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var images = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var ui = new SortedDictionary<string, SortedDictionary<(string Screen, string State), (string? Feedback, string Fallback)>>(StringComparer.Ordinal);
        await foreach (var e in svc.Ledger.ReadAsync(failedRunId, ct))
        {
            // A ui: failure is read whatever its outcome: the page check does not repair a page, it reports the failure.
            if (e.TaskId is null || e.Payload is null || (e.Outcome != "failed" && e.Kind != LedgerKinds.UiCheckFailed)) continue;
            using var doc = JsonDocument.Parse(e.Payload);
            string S(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            string? line = e.Kind switch
            {
                LedgerKinds.UiAcceptanceFailed =>
                    $"Frozen acceptance spec {S("spec")} (acceptance {S("ac")}{(e.Element is null ? "" : $" on element {e.Element}")}) failed on integration at {S("commit")}."
                    + (S("trace") is { Length: > 0 } t ? "\nTrace:\n" + (t.Length > TraceChars ? t[..TraceChars] + " …" : t) : ""),
                // Screen, criterion and state only: the holdout's data and the runner's output never reach a brief.
                LedgerKinds.HoldoutFailed =>
                    $"A hidden check of screen {S("screen")}{(S("criterion") is { Length: > 0 } cr ? $" (acceptance {cr})" : "")} failed in state \"{S("state")}\"{(e.Element is null ? "" : $" on element {e.Element}")}. "
                    + "The page does not render the data it will get; do not special-case sample values.",
                LedgerKinds.CiFailed =>
                    $"CI check \"{S("check")}\" failed{(S("commit") is { Length: > 0 } c ? $" at {c}" : "")}. End of its log:\n{S("log")}",
                LedgerKinds.TokenLintViolation =>
                    $"Raw value where a design token belongs: {S("file")}:{doc.RootElement.GetProperty("line")} {S("rule")}: {S("text")}",
                _ => null,
            };
            if (e.Kind == LedgerKinds.UiCheckFailed)
            {
                // Per (task, screen, state): the feedback text rides on one row of the state; the others only say which kind failed.
                var key = (S("screen"), S("state"));
                if (!ui.TryGetValue(e.TaskId, out var states)) ui[e.TaskId] = states = [];
                var had = states.GetValueOrDefault(key);
                var feedback = S("feedback") is { Length: > 0 } fb ? fb : had.Feedback;
                var detail = S("detail") is { Length: > 0 } d ? $": {d}" : "";
                states[key] = (feedback, had.Fallback + (had.Fallback is null or "" ? "" : "; ") + S("kind") + (S("placement") is { Length: > 0 } pl ? $" {pl}" : "") + detail);
                if (doc.RootElement.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var i in imgs.EnumerateArray())
                        if (i.GetString() is { } p)
                        {
                            if (!images.TryGetValue(e.TaskId, out var il)) images[e.TaskId] = il = [];
                            if (!il.Contains(p)) il.Add(p);
                        }
                }
                continue;
            }
            if (line is null) continue;
            if (!lines.TryGetValue(e.TaskId, out var list)) lines[e.TaskId] = list = [];
            if (!list.Contains(line)) list.Add(line);
        }
        foreach (var (task, states) in ui)
        {
            if (!lines.TryGetValue(task, out var list)) lines[task] = list = [];
            foreach (var ((screen, state), (feedback, fallback)) in states)
                list.Add($"The page check of screen {screen}{(state.Length > 0 ? $", state \"{state}\"" : "")} failed on integration.\n" + (feedback ?? "Failed: " + fallback));
        }
        if (lines.Count == 0) return null;
        return new RepairFile(failedRunId, lines.ToDictionary(kv => kv.Key, kv => Brief(kv.Value)),
            images.Count == 0 ? null : images.ToDictionary(kv => kv.Key, kv => kv.Value.Take(MaxImages).ToArray()));
    }

    private static string Brief(List<string> lines)
    {
        var sb = new StringBuilder("Your earlier work merged and the integrated branch then failed checks you cannot edit. "
            + "Change only what your task writes so these pass; do not touch the frozen specs.\n");
        foreach (var l in lines) sb.AppendLine().AppendLine("- " + l);
        return sb.ToString().TrimEnd();
    }

    /// How many repair runs lie behind this one: follows `repair.json`'s `Of` back to a run that is not itself a repair.
    public static int Rounds(FactorySettings s, string runId)
    {
        var rounds = 0;
        for (var seen = new HashSet<string>(); seen.Add(runId) && File.Exists(s.RepairPath(runId)); rounds++)
        {
            // An unreadable repair.json still marks a repair run; the chain behind it cannot be followed.
            if (Read(s.RepairPath(runId)) is not { } file) return rounds + 1;
            runId = file.Of;
        }
        return rounds;
    }

    private static RepairFile? Read(string path)
    {
        try { return JsonSerializer.Deserialize<RepairFile>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    /// Why a repair of `failedRun` is refused: its chain already used `max` rounds, so a person looks instead of the harness paying again.
    public static string? RoundsRefusal(FactorySettings s, string failedRun, int max) =>
        Rounds(s, failedRun) >= max ? $"run {failedRun} is already repair round {Rounds(s, failedRun)} (limit {max}, FACTORY_MAX_FIX_ROUNDS): a person should look at it" : null;

    public static string? Feedback(FactorySettings s, string runId, string taskId)
    {
        var path = s.RepairPath(runId);
        if (!File.Exists(path)) return null;
        return Read(path)?.Feedback?.GetValueOrDefault(taskId);
    }

    /// The pictures of a task's repair brief, read from the run dir's evidence (paths that leave it, or files that are gone, are skipped).
    public static IReadOnlyList<byte[]> Images(FactorySettings s, string runId, string taskId)
    {
        var path = s.RepairPath(runId);
        if (!File.Exists(path) || Read(path) is not { Images: { } all } file || !all.TryGetValue(taskId, out var rels)) return [];
        var from = s.RunDir(file.Of); // the pictures live in the failed run's dir, which `Of` names
        var root = Path.GetFullPath(from) + Path.DirectorySeparatorChar;
        return rels.Select(r => Path.GetFullPath(Path.Combine(from, r))).Where(f => f.StartsWith(root) && File.Exists(f)).Select(File.ReadAllBytes).ToList();
    }
}
