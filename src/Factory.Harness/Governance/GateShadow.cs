using System.Text.Json;
using Factory.Core.Gate;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.Harness.Governance;

public sealed record ShadowRow(string Ticket, string To, string Commit, string Kit, string Gate, bool Agrees, string Note);

/// Shadow mode against recorded cases: for each transition the Claude Code QA gate decided (`kit`:
/// allow|deny), what the harness gate would have decided. Nothing is enforced; every evaluation is ledgered.
/// A case's `bundle` is the evidence found for it; no Proof results when Proof.tryCreate refuses it
/// against `required`, and the Note says why.
public static class GateShadow
{
    public static async Task<IReadOnlyList<ShadowRow>> RunAsync(JiraTransitionGate gate, string runId, string json, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(json);
        var required = doc.RootElement.TryGetProperty("required", out var r)
            ? ListModule.OfSeq(r.EnumerateArray().Select(x => x.GetString()!))
            : FSharpList<string>.Empty;
        var rows = new List<ShadowRow>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string S(string name) => c.GetProperty(name).GetString()!;
            var proofs = new List<Proof>();
            var note = "";
            if (c.TryGetProperty("bundle", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                var result = ProofModule.tryCreate(required, Bundle(b));
                if (result.IsOk) proofs.Add(result.ResultValue);
                else note = string.Join("; ", result.ErrorValue);
            }
            var d = await gate.DecideAsync(runId, S("ticket"), S("to"), S("commit"), proofs, ct);
            var verdict = d.Allowed ? "allow" : "deny";
            rows.Add(new ShadowRow(S("ticket"), S("to"), S("commit"), S("kit"), verdict, verdict == S("kit"), note));
        }
        return rows;
    }

    private static ProofBundle Bundle(JsonElement b) => new(
        b.GetProperty("subject").GetString()!,
        b.GetProperty("commit").GetString()!,
        ListModule.OfSeq(b.GetProperty("checks").EnumerateArray().Select(k => new CheckResult(
            k.GetProperty("name").GetString()!,
            k.GetProperty("passed").GetBoolean(),
            k.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "",
            ListModule.OfSeq(Array.Empty<Uri>())))),
        ListModule.OfSeq(b.GetProperty("approvals").EnumerateArray().Select(a =>
            new Approval(a.GetProperty("by").GetString()!, a.GetProperty("source").GetString()!))));

    public static string Render(IReadOnlyList<ShadowRow> rows)
    {
        var agree = rows.Count(r => r.Agrees);
        var lines = new List<string> { $"{agree} of {rows.Count} agree with the kit" };
        foreach (var r in rows.OrderBy(r => r.Agrees))
            lines.Add($"{(r.Agrees ? "agree   " : "DISAGREE")} {r.Ticket} -> {r.To} @ {r.Commit}: kit {r.Kit}, gate {r.Gate}{(r.Note == "" ? "" : $" ({r.Note})")}");
        return string.Join('\n', lines) + "\n";
    }
}
