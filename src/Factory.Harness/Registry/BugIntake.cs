using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;

namespace Factory.Harness.Registry;

/// A bug reported against shipped work, with the failing acceptance spec that reproduces it, the
/// origin it was classified as and the gate that now prevents it.
public sealed record BugIntakeRequest(
    string Id, string Summary, string Spec, string Origin, string Reference, string Gate, string? Strengthens, string? CaughtBy);

/// Runs the Defect lifecycle (reproduce, classify, countermeasure) and writes the closed bug as a designc
/// `escape` row on its own run stream. Nothing is written unless every step holds, so a bug that has no
/// failing spec or no new gate never reaches the ledger as closed.
public static class BugIntake
{
    private static readonly string[] CaughtByValues = ["S1-lint", "S1-p", "S1-grill", "pipeline"];

    /// Null when the bug was recorded; otherwise why it could not be closed.
    public static async Task<string?> RecordAsync(
        string registryPath, BugIntakeRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (Close(request) is { } why) return why;
        await RunLedgerStream.AppendAsync(registryPath, "intake-" + request.Id, [Row(request, now)], ct);
        await LedgerFold.RunAsync(registryPath, ct);
        return null;
    }

    /// Walks the Defect lifecycle; the reason it cannot close, or null when it can.
    internal static string? Close(BugIntakeRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Summary))
            return "A bug needs an id and a summary.";
        if (string.IsNullOrWhiteSpace(r.Gate))
            return "A bug is not closed without a new or strengthened gate (--gate).";
        if (r.CaughtBy is not null && !CaughtByValues.Contains(r.CaughtBy))
            return $"Unknown caught-by '{r.CaughtBy}': expected {string.Join(", ", CaughtByValues)}.";

        var check = FailingCheckModule.create(r.Spec);
        if (check.IsError) return check.ErrorValue;
        var origin = DefectOriginModule.parse(r.Origin, r.Reference);
        if (origin.IsError) return origin.ErrorValue;

        var gate = new NewGate(r.Gate, r.Strengthens is null ? Microsoft.FSharp.Core.FSharpOption<string>.None : Microsoft.FSharp.Core.FSharpOption<string>.Some(r.Strengthens));
        var defect = DefectModule.report(new BugReport(r.Id, r.Summary));
        var done = DefectModule.reproduce(check.ResultValue, defect);
        if (done.IsError) return done.ErrorValue;
        var classified = DefectModule.classify(origin.ResultValue, done.ResultValue);
        if (classified.IsError) return classified.ErrorValue;
        var countered = DefectModule.close(gate, classified.ResultValue);
        return countered.IsError ? countered.ErrorValue : null;
    }

    private static RegistryEvent Row(BugIntakeRequest r, DateTimeOffset now)
    {
        string Body(string? eventId)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("kind", "escape");
                w.WriteString("id", r.Id);
                w.WriteString("description", r.Summary);
                w.WriteString("origin", r.Origin);
                if (r.CaughtBy is not null) w.WriteString("caught_by", r.CaughtBy);
                w.WriteString("source", "s2-intake");
                w.WriteStartArray("traces");
                w.WriteStringValue(r.Spec);
                w.WriteEndArray();
                w.WriteString("by", RegistryLedgerProjector.By);
                w.WriteString("system", "S2");
                w.WriteStartObject("payload");
                w.WriteString("reference", r.Reference);
                w.WriteString("gate", r.Gate);
                if (r.Strengthens is not null) w.WriteString("strengthens", r.Strengthens);
                w.WriteEndObject();
                w.WriteNumber("schema_version", 1);
                w.WriteNumber("ts", now.ToUnixTimeMilliseconds());
                if (eventId is not null) w.WriteString("event_id", eventId);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        var id = "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Body(null))))[..32];
        return new RegistryEvent("designc.jsonl", id, Body(id));
    }
}
