using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factory.Harness;

/// One row of the shared ledger. Same shape as sql/ledger.sql so the JSONL file and the
/// Postgres table are interchangeable. Payload is the full JSON detail for the event.
public sealed record LedgerEvent(
    string RunId,
    string System,
    string Stage,
    string Kind,
    string? TaskId = null,
    string? TeamId = null,
    string? Requirement = null,
    string? Element = null,
    string? Decision = null,
    string? Code = null,
    string? Severity = null,
    string? Outcome = null,
    string? Model = null,
    long? TokensIn = null,
    long? TokensOut = null,
    decimal? Cost = null,
    string? Payload = null)
{
    public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;
    public string? TraceId { get; init; } = Activity.Current?.TraceId.ToString();
}

/// Kinds used by System 2. System 4 queries on these, so treat them as a contract.
public static class LedgerKinds
{
    public const string Plan = "plan";              // planner attempt, validator verdict
    public const string Dispatch = "dispatch";
    public const string TaskEvent = "task";         // payload = encoded F# TaskEvent; replayed on resume
    public const string Turn = "turn";              // one agent turn: model, tokens, tool calls
    public const string ToolCall = "tool_call";     // allowed or denied by policy
    public const string Configurator = "configurator"; // routing decision before a turn: kind, tier, reason
    public const string Verify = "verify";
    public const string NeedsDecision = "needs_decision";
    public const string ScopeViolation = "scope_violation";
    public const string Review = "review";          // one review round: outcome, findings kept and dropped, independence
    public const string Merge = "merge";
    public const string ManualCheck = "manual_check"; // task + payload {check, by, outcome: done|deferred, note}: a person recorded a manual check (factory-s2 verify)
    public const string AsBuiltEdge = "asbuilt_edge"; // payload {from, to}: an element pair the code crosses with no relationship in the model (debt)
    public const string TokenLintViolation = "token_lint_violation"; // task + payload {file, line, rule, text}: a raw value where a design token belongs
    public const string UiAcceptanceFailed = "ui_acceptance_failed"; // element + payload {ac, spec, commit, trace}: a frozen Playwright spec failed on this task's element
    public const string CiFailed = "ci_failed";     // task + payload {check, log, files, commit}: CI failed after the run ended (factory-s2 ci-report); a repair run reads it
    public const string Diagnosis = "diagnosis";    // task + payload {finding}: the s2-diagnosis agent's explanation of a failed frozen spec, folded into the repair brief
    public const string Reproduction = "reproduction"; // payload {report, steps}: the s2-repro agent's steps for a reported escape; intake still needs a failing spec
    public const string UxScreenshots = "ux_screenshots"; // payload {command, shots:[{element,state,breakpoint,path}]}: freeze-time screenshots taken for one human review
    public const string UxJudge = "ux_judge";       // element + payload {state, breakpoint, hierarchy, clarity, consistency, note}: a gradient; outcome is always "gradient", never a gate
    public const string Run = "run";
    public const string RunStarted = "run_started"; // an attempt at the run began (a resume is a new attempt)
    public const string RunAborted = "run_aborted"; // payload {status, detail}: an attempt ended without reaching Finalize (crash, cancel, workflow error)
}

public interface ILedger
{
    ValueTask AppendAsync(LedgerEvent e, CancellationToken ct = default);
    IAsyncEnumerable<LedgerEvent> ReadAsync(string runId, CancellationToken ct = default);
}

/// Append-only JSON Lines ledger for local runs and the Phase 2 slice. Swap for the Postgres
/// implementation once the platform decision (Postgres vs Azure SQL) is made; nothing else changes.
public sealed class JsonlLedger(string path) : ILedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask AppendAsync(LedgerEvent e, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(e, Json) + "\n";
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.AppendAllTextAsync(path, line, ct);
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<LedgerEvent> ReadAsync(string runId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(path)) yield break;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var e = JsonSerializer.Deserialize<LedgerEvent>(line, Json);
            if (e is null || e.RunId != runId) continue;
            // TraceId defaults to the current trace, which is the writer's and never the reader's: a row with none keeps none.
            if (e.TraceId is not null && !HasTraceId(line)) e = e with { TraceId = null };
            yield return e;
        }
    }

    private static bool HasTraceId(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("trace_id", out var t) && t.ValueKind == JsonValueKind.String;
    }
}

public static class LedgerExtensions
{
    /// Payloads of a task's state-machine events, in order: the input to Core.Replay.
    public static async Task<List<string>> TaskEventPayloadsAsync(this ILedger ledger, string runId, string taskId, CancellationToken ct)
    {
        var result = new List<string>();
        await foreach (var e in ledger.ReadAsync(runId, ct))
            if (e.Kind == LedgerKinds.TaskEvent && e.TaskId == taskId && e.Payload is not null)
                result.Add(e.Payload);
        return result;
    }
}
