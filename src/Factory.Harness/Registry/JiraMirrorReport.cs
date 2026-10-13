using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.Harness.Registry;

/// The one-way Jira mirror (ledger -> Jira). S2 holds no Jira client: it plans the transitions from a snapshot of the
/// issues' statuses and leaves them for whatever can write Jira (an agent with the Atlassian MCP, a person). A Jira
/// status the mirror did not write is drift: it is never overwritten, and it is flagged to a person as an
/// `item_awaiting_human` row so it shows in the digest's Waiting bucket.
public static class JiraMirrorReport
{
    public const string RunStream = "jira-mirror";

    public static async Task<IReadOnlyList<MirrorAction>> PlanAsync(
        string registryPath, string? repoPath, string baseBranch, IReadOnlyDictionary<string, string> jira,
        IReadOnlyDictionary<string, string> mirrored, CancellationToken ct)
    {
        var registry = await WorkRegistryReader.ReadAsync(registryPath, ct);
        var onMain = await WorkDigestReport.OnMainAsync(registry, repoPath, baseBranch, ct);
        var candidates = Board.candidates(
            ListModule.OfSeq(registry.Items),
            ListModule.OfSeq(registry.Facts.Select(f => Tuple.Create(f.Ts, f.Fact))),
            FuncConvert.FromFunc<string, FSharpOption<bool>>(c =>
                onMain.GetValueOrDefault(c) is { } b ? FSharpOption<bool>.Some(b) : FSharpOption<bool>.None));
        var plan = JiraMirror.plan(
            ListModule.OfSeq(candidates.Select(c => Tuple.Create(c.Item, c.State))),
            MapModule.OfSeq(jira.Select(kv => Tuple.Create(kv.Key, kv.Value))),
            MapModule.OfSeq(mirrored.Select(kv => Tuple.Create(kv.Key, kv.Value))));
        return plan.ToList();
    }

    /// The Jira keys the registry's items mirror to (what a REST read must fetch).
    public static async Task<IReadOnlyList<string>> KeysAsync(string registryPath, CancellationToken ct)
    {
        var registry = await WorkRegistryReader.ReadAsync(registryPath, ct);
        return registry.Items.Select(i => JiraMirror.key(i)).Where(k => FSharpOption<string>.get_IsSome(k)).Select(k => k.Value).Distinct().Order().ToList();
    }

    /// Applies the transitions through REST. Returns what was moved (key -> status) and what could not be (messages, no secrets).
    public static async Task<(Dictionary<string, string> Moved, List<string> Failed)> ApplyAsync(
        JiraRestClient client, IEnumerable<MirrorAction> plan, CancellationToken ct)
    {
        var moved = new Dictionary<string, string>();
        var failed = new List<string>();
        foreach (var t in plan.OfType<MirrorAction.Transition>())
        {
            try
            {
                if (await client.TransitionAsync(t.key, t.target, ct) is { } why) failed.Add(why);
                else moved[t.key] = t.target;
            }
            catch (HttpRequestException e) { failed.Add($"{t.key}: Jira answered {(int?)e.StatusCode}"); }
        }
        return (moved, failed);
    }

    /// Writes one `item_awaiting_human` row per drifted issue (same drift, same event id: flagged once).
    public static async Task<int> FlagDriftAsync(
        string registryPath, IEnumerable<MirrorAction> plan, DateTimeOffset now, CancellationToken ct)
    {
        var rows = plan.OfType<MirrorAction.Drift>().Select(d => Row(d, now)).ToList();
        if (rows.Count == 0) return 0;
        await RunLedgerStream.AppendAsync(registryPath, RunStream, rows, ct);
        await LedgerFold.RunAsync(registryPath, ct);
        return rows.Count;
    }

    internal static string Reason(MirrorAction.Drift d) =>
        $"Jira {d.key} says '{d.jira}' but the ledger says '{d.wanted}': decide which is right; the mirror will not overwrite Jira.";

    private static RegistryEvent Row(MirrorAction.Drift d, DateTimeOffset now)
    {
        string Body(string? eventId)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("kind", "item_awaiting_human");
                w.WriteString("item", d.item);
                w.WriteString("reason", Reason(d));
                w.WriteString("by", RegistryLedgerProjector.By);
                w.WriteString("system", "S2");
                w.WriteNumber("schema_version", 1);
                w.WriteNumber("ts", now.ToUnixTimeMilliseconds());
                if (eventId is not null) w.WriteString("event_id", eventId);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        // The id hashes item + reason (not ts), so an unchanged drift is flagged once however often the mirror runs.
        var id = "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(d.item + "\n" + Reason(d))))[..32];
        return new RegistryEvent("pipeline.jsonl", id, Body(id));
    }

    public static string Render(IEnumerable<MirrorAction> plan)
    {
        var sb = new StringBuilder();
        foreach (var a in plan)
            sb.AppendLine(a switch
            {
                MirrorAction.Transition t => $"  set    {t.key} ({t.item}): {t.from} -> {t.target}",
                MirrorAction.Drift d => $"  DRIFT  {d.key} ({d.item}): Jira '{d.jira}', ledger '{d.wanted}'",
                MirrorAction.NotInJira n => $"  absent {n.key} ({n.item}): not in the snapshot",
                MirrorAction.InSync s => $"  ok     {s.key}",
                _ => "",
            });
        return sb.ToString();
    }

    /// `{ "HRE-7": "In Progress", ... }`; a missing file is an empty map.
    public static async Task<Dictionary<string, string>> ReadMapAsync(string path, CancellationToken ct) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct)) ?? []
            : [];
}
