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
    public const string AsBuiltEdge = "asbuilt_edge"; // payload {from, to}: an element pair the code crosses with no relationship in the model (debt)
    public const string TokenLintViolation = "token_lint_violation"; // task + payload {file, line, rule, text}: a raw value where a design token belongs
    public const string UiAcceptanceFailed = "ui_acceptance_failed"; // element + payload {ac, spec, commit, trace}: a frozen Playwright spec failed on this task's element
    public const string Run = "run";
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
            if (e is not null && e.RunId == runId) yield return e;
        }
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
