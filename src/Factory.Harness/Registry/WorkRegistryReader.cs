using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Registry;

/// The registry's work items and the ledger facts that decide where each one stands. Problems are
/// plain sentences (designc's Work.validate wording); an item with a problem is left out of Items.
public sealed record WorkRegistry(
    IReadOnlyList<WorkItem> Items,
    IReadOnlyList<string> Problems,
    IReadOnlyList<(long Ts, LedgerFact Fact)> Facts);

/// Reads designc's `work/<id>.yaml` (format owned by Designc, not forked here) through
/// `WorkItem.create`, and the `ledger/*.jsonl` rows the work digest cares about.
public static class WorkRegistryReader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static async Task<WorkRegistry> ReadAsync(string registryPath, CancellationToken ct)
    {
        var problems = new List<string>();
        var requirements = await RequirementsAsync(registryPath, ct);
        var drafts = new List<WorkDraft>();
        var dir = Path.Combine(registryPath, "work");
        if (Directory.Exists(dir))
            foreach (var file in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var map = Yaml.Deserialize<Dictionary<string, object?>>(await File.ReadAllTextAsync(file, ct)) ?? [];
                    drafts.Add(Draft(map));
                }
                catch (Exception e) when (e is YamlDotNet.Core.YamlException or InvalidCastException)
                {
                    problems.Add($"{Path.GetFileName(file)}: not a readable work item ({e.Message})");
                }
            }

        var ids = drafts.Select(d => d.Id).ToHashSet();
        var items = new List<WorkItem>();
        foreach (var d in drafts)
        {
            var own = new List<string>();
            if (d.Requirement is { } rq && !string.IsNullOrWhiteSpace(rq.Value))
            {
                if (!requirements.TryGetValue(rq.Value, out var elements)) own.Add($"{d.Id}: requirement {rq.Value} does not exist in requirements/.");
                else own.AddRange(d.Elements.Where(e => !elements.Contains(e)).Select(e =>
                    $"{d.Id}: {e} is not an element of requirement {rq.Value}. Add it to the requirement, or take it out of this item."));
            }
            own.AddRange(d.DependsOn.Where(x => !ids.Contains(x)).Select(x => $"{d.Id}: depends_on {x} is not a work item."));
            var made = WorkItemModule.create(d);
            if (made.IsError) own.AddRange(made.ErrorValue.Select(e => WorkErrorModule.describe(d.Id, e)));
            if (own.Count > 0) problems.AddRange(own);
            else items.Add(made.ResultValue);
        }
        return new WorkRegistry(items, problems, await FactsAsync(registryPath, ct));
    }

    private static async Task<Dictionary<string, HashSet<string>>> RequirementsAsync(string registryPath, CancellationToken ct)
    {
        var map = new Dictionary<string, HashSet<string>>();
        var dir = Path.Combine(registryPath, "requirements");
        if (!Directory.Exists(dir)) return map;
        foreach (var file in Directory.GetFiles(dir, "*.yaml"))
        {
            try
            {
                var m = Yaml.Deserialize<Dictionary<string, object?>>(await File.ReadAllTextAsync(file, ct)) ?? [];
                if (m.GetValueOrDefault("id")?.ToString() is { Length: > 0 } id) map[id] = Strings(m.GetValueOrDefault("elements")).ToHashSet();
            }
            catch (YamlDotNet.Core.YamlException) { }
        }
        return map;
    }

    private static WorkDraft Draft(Dictionary<string, object?> m)
    {
        FSharpOption<string> Opt(string key) => m.GetValueOrDefault(key)?.ToString() is { } s ? FSharpOption<string>.Some(s) : FSharpOption<string>.None;

        FSharpOption<Tuple<string, string>> target = FSharpOption<Tuple<string, string>>.None;
        if (m.GetValueOrDefault("target") is IDictionary<object, object> t && t.Count > 0)
        {
            var kv = t.First();
            target = FSharpOption<Tuple<string, string>>.Some(Tuple.Create(kv.Key.ToString() ?? "", kv.Value?.ToString() ?? ""));
        }

        var doneWhen = new List<Tuple<string, string>>();
        if (m.GetValueOrDefault("done_when") is IEnumerable<object> entries)
            foreach (var e in entries.OfType<IDictionary<object, object>>())
            {
                // An entry with several (or no) kinds is passed on as "?" so WorkItem.create reports it by position.
                var kv = e.Count == 1 ? e.First() : default;
                doneWhen.Add(Tuple.Create(e.Count == 1 ? kv.Key.ToString() ?? "" : "?", e.Count == 1 ? kv.Value?.ToString() ?? "" : ""));
            }

        return new WorkDraft(
            m.GetValueOrDefault("id")?.ToString() ?? "",
            Opt("requirement"),
            ListModule.OfSeq(Strings(m.GetValueOrDefault("elements"))),
            Opt("repo"),
            target,
            ListModule.OfSeq(Strings(m.GetValueOrDefault("depends_on"))),
            ListModule.OfSeq(Strings(m.GetValueOrDefault("writes"))),
            ListModule.OfSeq(doneWhen));
    }

    private static IEnumerable<string> Strings(object? v) =>
        v is IEnumerable<object> xs ? xs.Select(x => x?.ToString() ?? "") : [];

    private static async Task<IReadOnlyList<(long, LedgerFact)>> FactsAsync(string registryPath, CancellationToken ct)
    {
        var facts = new List<(long, LedgerFact)>();
        var dir = Path.Combine(registryPath, "ledger");
        if (!Directory.Exists(dir)) return facts;
        foreach (var file in Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
                if (Fact(line) is { } f) facts.Add(f);
        return facts.OrderBy(f => f.Item1).ToList();
    }

    internal static (long, LedgerFact)? Fact(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            FSharpOption<string> O(string n) => S(n) is { } s ? FSharpOption<string>.Some(s) : FSharpOption<string>.None;
            var ts = r.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0L;
            LedgerFact? fact = S("kind") switch
            {
                "needs_decision" when S("status") == "open" && S("id") is { } id =>
                    LedgerFact.NewNeedsDecisionOpened(id, O("task"), O("element"), S("question") ?? ""),
                "needs_decision" or "needs_decision_resolved" when S("id") is { } id && (S("status") ?? "resolved") == "resolved" =>
                    LedgerFact.NewNeedsDecisionResolved(id),
                "item_awaiting_human" when S("item") is { } i => LedgerFact.NewItemAwaitingHuman(i, O("reason")),
                "item_pulled" when S("item") is { } i => LedgerFact.NewItemPulled(i),
                "item_gate" when S("item") is { } i && S("gate") is { } g =>
                    LedgerFact.NewItemGate(i, g, S("outcome") == "pass", O("commit")),
                "item_done" when S("item") is { } i =>
                    LedgerFact.NewItemDone(i, O("commit"),
                        r.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.Array ? ev.GetArrayLength() : 0),
                "ui_acceptance_failed" when S("task") is { } k && S("ac") is { } ac =>
                    LedgerFact.NewAcceptanceFailed(k, ac, S("element") ?? "", S("commit") ?? ""),
                "item_conflict_deferred" when S("item") is { } i => LedgerFact.NewConflictDeferred(i, O("reason")),
                "item_recheck" when S("item") is { } i =>
                    LedgerFact.NewItemRecheck(i, r.TryGetProperty("stale", out var st) && st.ValueKind == JsonValueKind.True),
                "escape" when S("id") is { } e => LedgerFact.NewEscapeRecorded(e, O("origin")),
                "wip_limit_changed" when r.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) => LedgerFact.NewWipLimitChanged(n),
                _ => null,
            };
            return fact is null ? null : (ts, fact);
        }
        catch (JsonException) { return null; }
    }
}
