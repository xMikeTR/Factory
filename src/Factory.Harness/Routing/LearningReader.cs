using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.Harness.Routing;

using Core = Factory.Core.Core;

/// HalfLifeDays: evidence weighs 0.5 ** (age / half-life). LearnReviewer: count reviewer verdicts (weak evidence).
public sealed record LearningSettings(double HalfLifeDays = 30, bool LearnReviewer = false);

/// Rebuilds the router's stats from the run ledger, the source of truth. Attribution (which model a verify result
/// is credited to) is the pure `Learning.attribute`; this class only maps rows to its input and caches the result.
///
/// Cache: {stateDir}/router-stats.json holds the attributed attempts, keyed by the ledger's length and `learnReviewer`.
/// The registry's ledger size is part of the key too (a parity result arriving later changes attribution).
/// It is a cache only: a missing, unreadable or stale file is rebuilt from the ledger. Decay and the catalog filter
/// are applied after the cache, so neither is part of its key.
public static class LearningReader
{
    private const string CacheFile = "router-stats.json";

    public static async Task<FSharpMap<StatsKey, Counts>> StatsAsync(
        string stateDir, Func<string, bool> known, LearningSettings settings, DateTimeOffset now, CancellationToken ct,
        string? registryPath = null)
    {
        var attempts = await AttemptsAsync(stateDir, settings.LearnReviewer, registryPath, ct);
        return Core.FoldAttempts(now.UtcDateTime, settings.HalfLifeDays, attempts.Where(a => known(a.Item1.Model)));
    }

    private sealed record CachedAttempt(string Model, string Role, string? Repo, string Size, bool Passed, int TokensIn, int TokensOut, DateTime Ts);
    private sealed record Cache(long LedgerBytes, long RegistryBytes, bool LearnReviewer, List<CachedAttempt> Attempts);

    private static async Task<List<Tuple<StatsKey, bool, int, int, DateTime>>> AttemptsAsync(string stateDir, bool learnReviewer, string? registryPath, CancellationToken ct)
    {
        var ledgerPath = Path.Combine(stateDir, "ledger.jsonl");
        if (!File.Exists(ledgerPath)) return [];
        var bytes = new FileInfo(ledgerPath).Length;
        var registryBytes = RegistryParity.Bytes(registryPath);
        var cachePath = Path.Combine(stateDir, CacheFile);

        if (ReadCache(cachePath) is { } cache && cache.LedgerBytes == bytes && cache.RegistryBytes == registryBytes && cache.LearnReviewer == learnReviewer)
            return cache.Attempts.Select(FromCached).ToList();

        var events = new List<LearnEvent>();
        await foreach (var row in new JsonlLedger(ledgerPath).ReadAllAsync(ct))
            if (Map(row) is { } e) events.Add(e);
        events.AddRange(await RegistryParity.JudgeAsync(stateDir, registryPath, events, ct));
        // Everything is attributed here; the catalog filter comes after the cache.
        var attempts = Core.Attribute(_ => true, learnReviewer, events).ToList();

        try
        {
            var cached = attempts.Select(ToCached).ToList();
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new Cache(bytes, registryBytes, learnReviewer, cached)), ct);
        }
        catch (IOException) { /* a cache that cannot be written is only slower */ }
        return attempts.Select(a => Tuple.Create(a.Item1, a.Item2, a.Item3, a.Item4, a.Item5)).ToList();
    }

    private static Cache? ReadCache(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    private static CachedAttempt ToCached(Tuple<StatsKey, bool, int, int, DateTime> a) =>
        new(a.Item1.Model, a.Item1.Role.ToString(), a.Item1.Repo?.Value, a.Item1.Size.ToString(), a.Item2, a.Item3, a.Item4, a.Item5);

    private static Tuple<StatsKey, bool, int, int, DateTime> FromCached(CachedAttempt c) =>
        Tuple.Create(new StatsKey(c.Model, ParseRole(c.Role)!, c.Repo is null ? FSharpOption<string>.None : FSharpOption<string>.Some(c.Repo), ParseSize(c.Size)!),
            c.Passed, c.TokensIn, c.TokensOut, c.Ts);

    // Task ids repeat across runs, so the learning events are keyed run/task.
    private static string Key(LedgerEvent e) => $"{e.RunId}/{e.TaskId}";

    private static LearnEvent? Map(LedgerEvent e)
    {
        if (e.TaskId is null) return null;
        switch (e.Kind)
        {
            case LedgerKinds.Route:
                if (Str(e, "role") is { } r && ParseRole(RoleFromName(r)) is { } role && Str(e, "size") is { } s && ParseSize(s) is { } size)
                    return LearnEvent.NewRouted(new RouteFact(Key(e), role, Str(e, "repo") is { } repo ? FSharpOption<string>.Some(repo) : FSharpOption<string>.None, size));
                return null;
            case LedgerKinds.Turn when e.Stage == "act":
                if (Str(e, "model_id") is { } model && Str(e, "role") is { } tr && ParseRole(RoleFromName(tr)) is { } turnRole)
                    return LearnEvent.NewTurn(new TurnFact(Key(e), turnRole, model, (int)(e.TokensIn ?? 0), (int)(e.TokensOut ?? 0), e.Ts.UtcDateTime));
                return null;
            case LedgerKinds.TaskEvent:
                return Str(e, "type") switch
                {
                    "verify_completed" => LearnEvent.NewVerified(new VerifyFact(Key(e), e.Outcome == "Reviewing", e.Ts.UtcDateTime)),
                    "tests_authored" => LearnEvent.NewVerified(new VerifyFact(Key(e), true, e.Ts.UtcDateTime)),
                    _ => null,
                };
            case LedgerKinds.Review when e.Outcome is "passed" or "blocked":
                return Str(e, "model_id") is { } rm ? LearnEvent.NewReviewed(new ReviewFact(Key(e), rm, e.Outcome == "passed", e.Ts.UtcDateTime)) : null;
            default: return null;
        }
    }

    private static string? Str(LedgerEvent e, string name)
    {
        if (e.Payload is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string RoleFromName(string roleConstant) => roleConstant switch
    {
        Roles.Implementer => "Implementer",
        Roles.TestAuthor => "TestAuthor",
        Roles.TaskPlanner => "TaskPlanner",
        Roles.Reviewer => "Reviewer",
        Roles.Planner => "Planner",
        Roles.Qa => "Qa",
        _ => roleConstant,
    };

    private static Role? ParseRole(string name) => name switch
    {
        "Implementer" => Role.Implementer, "TestAuthor" => Role.TestAuthor, "TaskPlanner" => Role.TaskPlanner,
        "Reviewer" => Role.Reviewer, "Planner" => Role.Planner, "Qa" => Role.Qa, _ => null,
    };

    private static SizeBucket? ParseSize(string name) => name switch
    {
        "S" => SizeBucket.S, "M" => SizeBucket.M, "L" => SizeBucket.L, _ => null,
    };
}
