using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Routing;
using Xunit;

namespace Factory.Integration.Tests.Routing;

public sealed class LearningReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("learn-").FullName;
    private readonly JsonlLedger _ledger;
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    public LearningReaderTests() => _ledger = new JsonlLedger(Path.Combine(_dir, "ledger.jsonl"));
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static bool Known(string id) => id is "small" or "std" or "top";

    private ValueTask Row(LedgerEvent e) => _ledger.AppendAsync(e);

    private ValueTask Route(string run, string task, string role = Roles.Implementer, string size = "M") =>
        Row(new LedgerEvent(run, "S2", "route", LedgerKinds.Route, TaskId: task, Payload: JsonSerializer.Serialize(new { role, size })) { Ts = Now });

    private ValueTask Act(string run, string task, string? modelId, int tin = 100, int tout = 10, string role = Roles.Implementer) =>
        Row(new LedgerEvent(run, "S2", "act", LedgerKinds.Turn, TaskId: task, TokensIn: tin, TokensOut: tout,
            Payload: JsonSerializer.Serialize(modelId is null ? new Dictionary<string, object> { ["role"] = role } : new Dictionary<string, object> { ["role"] = role, ["model_id"] = modelId })) { Ts = Now });

    private ValueTask Verify(string run, string task, bool passed) =>
        Row(new LedgerEvent(run, "S2", "task", LedgerKinds.TaskEvent, TaskId: task, Outcome: passed ? "Reviewing" : "Ready",
            Payload: JsonSerializer.Serialize(new { type = "verify_completed" })) { Ts = Now });

    private ValueTask Review(string run, string task, string? modelId, bool passed) =>
        Row(new LedgerEvent(run, "S2", "review", LedgerKinds.Review, TaskId: task, Outcome: passed ? "passed" : "blocked",
            Payload: JsonSerializer.Serialize(modelId is null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["model_id"] = modelId })) { Ts = Now });

    private static Counts? Find(FSharpMapOfStats stats, string model, Role role, SizeBucket size) => stats.Find(model, role, size);

    private async Task<FSharpMapOfStats> Load(bool learnReviewer = false) =>
        new(await LearningReader.StatsAsync(_dir, Known, new LearningSettings(HalfLifeDays: 30, LearnReviewer: learnReviewer), Now, default));

    [Fact]
    public async Task Stats_come_from_fixture_ledgers_one_attempt_per_verify()
    {
        await Route("r1", "T-1");
        await Act("r1", "T-1", "small"); await Verify("r1", "T-1", false);
        await Act("r1", "T-1", "std");   await Verify("r1", "T-1", true);

        var stats = await Load();
        Assert.Equal(1.0, Find(stats, "small", Role.Implementer, SizeBucket.M)!.Failures, 6);
        Assert.Equal(1.0, Find(stats, "std", Role.Implementer, SizeBucket.M)!.Successes, 6);
        Assert.Equal(100.0, Find(stats, "std", Role.Implementer, SizeBucket.M)!.MeanTokensIn);
    }

    [Fact]
    public async Task The_same_task_id_in_two_runs_does_not_mix()
    {
        await Route("r1", "T-1"); await Route("r2", "T-1");
        await Act("r1", "T-1", "small");
        await Act("r2", "T-1", "std");
        await Verify("r2", "T-1", true);
        await Verify("r1", "T-1", false);

        var stats = await Load();
        Assert.Equal(1.0, Find(stats, "small", Role.Implementer, SizeBucket.M)!.Failures, 6);
        Assert.Equal(1.0, Find(stats, "std", Role.Implementer, SizeBucket.M)!.Successes, 6);
    }

    [Fact]
    public async Task Turns_without_a_model_id_and_unknown_models_give_no_evidence()
    {
        await Route("r1", "T-1");
        await Act("r1", "T-1", null);      await Verify("r1", "T-1", true);   // before routing existed: no model_id
        await Act("r1", "T-1", "ghost");   await Verify("r1", "T-1", true);   // not in the catalog any more

        Assert.Empty((await Load()).Keys());
    }

    [Fact]
    public async Task The_size_comes_from_the_route_row_and_the_role_from_the_turn()
    {
        await Route("r1", "T-1", Roles.TestAuthor, "L");
        await Act("r1", "T-1", "small", role: Roles.TestAuthor); await Verify("r1", "T-1", true);

        Assert.NotNull(Find(await Load(), "small", Role.TestAuthor, SizeBucket.L));
    }

    [Fact]
    public async Task A_reviewer_verdict_counts_only_when_learnReviewer_is_on()
    {
        await Route("r1", "T-1", Roles.Reviewer, "M");
        await Review("r1", "T-1", "top", passed: true);

        Assert.Empty((await Load(learnReviewer: false)).Keys());
        Assert.Equal(1.0, Find(await Load(learnReviewer: true), "top", Role.Reviewer, SizeBucket.M)!.Successes, 6);
    }

    [Fact]
    public async Task The_cache_is_written_and_rebuilt_when_the_ledger_grows()
    {
        await Route("r1", "T-1");
        await Act("r1", "T-1", "small"); await Verify("r1", "T-1", true);
        var first = await Load();
        Assert.True(File.Exists(Path.Combine(_dir, "router-stats.json")));
        Assert.Equal(1.0, Find(first, "small", Role.Implementer, SizeBucket.M)!.Successes, 6);

        await Route("r1", "T-2");
        await Act("r1", "T-2", "small"); await Verify("r1", "T-2", true);
        Assert.Equal(2.0, Find(await Load(), "small", Role.Implementer, SizeBucket.M)!.Successes, 6);
    }

    [Fact]
    public async Task A_fresh_cache_is_used_not_the_ledger_and_a_changed_setting_rebuilds_it()
    {
        await Route("r1", "T-1");
        await Act("r1", "T-1", "small"); await Verify("r1", "T-1", true);
        await Load();
        // Poison the cache but keep its key: a fresh cache must be trusted.
        var path = Path.Combine(_dir, "router-stats.json");
        var text = File.ReadAllText(path);
        Assert.Contains("\"small\"", text);
        File.WriteAllText(path, text.Replace("\"small\"", "\"std\""));
        Assert.NotNull(Find(await Load(), "std", Role.Implementer, SizeBucket.M));
        // learnReviewer is part of the key: flipping it rebuilds from the ledger, and the poison is gone.
        Assert.Null(Find(await Load(learnReviewer: true), "std", Role.Implementer, SizeBucket.M));
    }

    [Fact]
    public async Task No_ledger_means_no_stats()
    {
        Assert.Empty((await Load()).Keys());
    }

    [Fact]
    public async Task Old_evidence_counts_for_less()
    {
        await Row(new LedgerEvent("r1", "S2", "route", LedgerKinds.Route, TaskId: "T-1", Payload: JsonSerializer.Serialize(new { role = Roles.Implementer, size = "M" })) { Ts = Now.AddDays(-60) });
        await Row(new LedgerEvent("r1", "S2", "act", LedgerKinds.Turn, TaskId: "T-1", TokensIn: 1, TokensOut: 1, Payload: JsonSerializer.Serialize(new { role = Roles.Implementer, model_id = "small" })) { Ts = Now.AddDays(-60) });
        await Row(new LedgerEvent("r1", "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-1", Outcome: "Reviewing", Payload: JsonSerializer.Serialize(new { type = "verify_completed" })) { Ts = Now.AddDays(-60) });

        Assert.Equal(0.25, Find(await Load(), "small", Role.Implementer, SizeBucket.M)!.Successes, 6);
    }

    // --- 7c: a Ui task's verify pass needs its item's parity (tiers 1 and 2) to pass, judged after `factory-s2 parity` ---

    private string Registry => Path.Combine(_dir, "registry");

    private async Task WriteRunHandoverAsync(string run)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "runs", run));
        await File.WriteAllTextAsync(Path.Combine(_dir, "runs", run, "handover.json"), "{\"id\":\"h-1\"}");
    }

    private const string HandoverId = "h-1";

    private void RegistryRows(params object[] rows)
    {
        Directory.CreateDirectory(Path.Combine(Registry, "ledger"));
        File.WriteAllLines(Path.Combine(Registry, "ledger", "pipeline.jsonl"), rows.Select(r => JsonSerializer.Serialize(r)));
    }

    private async Task<FSharpMapOfStats> LoadWithParity() =>
        new(await LearningReader.StatsAsync(_dir, Known, new LearningSettings(), Now, default, Registry));

    private async Task UiVerifyPassAsync()
    {
        await Row(new LedgerEvent("r1", "S2", "route", LedgerKinds.Route, TaskId: "T-1",
            Payload: JsonSerializer.Serialize(new { role = Roles.Implementer, size = "M", repo = "Ui" })) { Ts = Now });
        await Act("r1", "T-1", "small");
        await Verify("r1", "T-1", true);
        await WriteRunHandoverAsync("r1");
    }

    private static Counts? UiCounts(FSharpMapOfStats stats) => stats.Find("small", Role.Implementer, SizeBucket.M, "Ui");

    [Fact]
    public async Task A_Ui_verify_pass_is_a_failure_when_its_items_parity_failed()
    {
        await UiVerifyPassAsync();
        RegistryRows(new { kind = "handover", item = "W-1", handover = HandoverId },
            new { kind = "parity_result", scenario = "s1", commit = "c1", outcome = "pass", item = "W-1", ts = 1 },
            new { kind = "parity_result", scenario = "s2", commit = "c1", outcome = "fail", item = "W-1", ts = 2 });

        var counts = UiCounts(await LoadWithParity())!;
        Assert.Equal(1.0, counts.Failures, 6);
        Assert.Equal(0.0, counts.Successes, 6);
    }

    [Fact]
    public async Task A_failed_visual_tier_fails_the_item_even_when_tier_one_passed()
    {
        await UiVerifyPassAsync();
        RegistryRows(new { kind = "handover", item = "W-1", handover = HandoverId },
            new { kind = "parity_result", scenario = "s1", commit = "c1", outcome = "pass", item = "W-1", ts = 1, visual = new { ratio = 0.5, threshold = 0.001, outcome = "fail" } });

        Assert.Equal(1.0, UiCounts(await LoadWithParity())!.Failures, 6);
    }

    [Fact]
    public async Task A_Ui_verify_pass_stays_a_success_when_parity_passed_was_not_run_or_a_later_run_fixed_it()
    {
        await UiVerifyPassAsync();
        RegistryRows(new { kind = "handover", item = "W-1", handover = HandoverId },
            new { kind = "parity_result", scenario = "s1", commit = "c1", outcome = "fail", item = "W-1", ts = 1 },
            new { kind = "parity_result", scenario = "s1", commit = "c2", outcome = "pass", item = "W-1", ts = 2 });
        Assert.Equal(1.0, UiCounts(await LoadWithParity())!.Successes, 6);          // the latest result per scenario counts

        RegistryRows(new { kind = "handover", item = "W-1", handover = HandoverId });  // parity never ran
        Assert.Equal(1.0, UiCounts(await LoadWithParity())!.Successes, 6);
        Assert.Equal(1.0, UiCounts(await Load())!.Successes, 6);                       // no registry given: as before
    }

    [Fact]
    public async Task Api_tasks_are_not_judged_by_parity_and_a_new_parity_row_invalidates_the_cache()
    {
        await Row(new LedgerEvent("r1", "S2", "route", LedgerKinds.Route, TaskId: "T-1",
            Payload: JsonSerializer.Serialize(new { role = Roles.Implementer, size = "M", repo = "Api" })) { Ts = Now });
        await Act("r1", "T-1", "small");
        await Verify("r1", "T-1", true);
        await WriteRunHandoverAsync("r1");
        RegistryRows(new { kind = "handover", item = "W-1", handover = HandoverId },
            new { kind = "parity_result", scenario = "s1", commit = "c1", outcome = "fail", item = "W-1", ts = 1 });
        Assert.Equal(1.0, (await LoadWithParity()).Find("small", Role.Implementer, SizeBucket.M, "Api")!.Successes, 6);

        // the Ui case: parity passes first (cached), then a failing row arrives with the ledger unchanged
        await Row(new LedgerEvent("r1", "S2", "route", LedgerKinds.Route, TaskId: "T-2",
            Payload: JsonSerializer.Serialize(new { role = Roles.Implementer, size = "M", repo = "Ui" })) { Ts = Now });
        await Act("r1", "T-2", "small");
        await Verify("r1", "T-2", true);
        RegistryRows(new { kind = "handover", item = "W-1", handover = HandoverId },
            new { kind = "parity_result", scenario = "s1", commit = "c1", outcome = "pass", item = "W-1", ts = 1 });
        Assert.Equal(1.0, UiCounts(await LoadWithParity())!.Successes, 6);
        RegistryRows(new { kind = "handover", item = "W-1", handover = HandoverId },
            new { kind = "parity_result", scenario = "s1", commit = "c1", outcome = "pass", item = "W-1", ts = 1 },
            new { kind = "parity_result", scenario = "s2", commit = "c1", outcome = "fail", item = "W-1", ts = 3 });
        Assert.Equal(1.0, UiCounts(await LoadWithParity())!.Failures, 6);
    }
}

/// Test-side view of the F# stats map.
internal readonly record struct FSharpMapOfStats(Microsoft.FSharp.Collections.FSharpMap<StatsKey, Counts> Map)
{
    public Counts? Find(string model, Role role, SizeBucket size, string? repo = null) =>
        Map.TryGetValue(new StatsKey(model, role, repo is null ? Microsoft.FSharp.Core.FSharpOption<string>.None : Microsoft.FSharp.Core.FSharpOption<string>.Some(repo), size), out var c) ? c : null;

    public IEnumerable<StatsKey> Keys() => Map.Select(kv => kv.Key);
}
