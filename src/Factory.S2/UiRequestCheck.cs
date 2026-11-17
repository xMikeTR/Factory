using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.S2;

/// F9: reads the request log the generated page test wrote (`.factory/requests/<screen>/<state>.json`) and the legacy baseline's
/// requests (registry `parity/baselines/<scenario>/legacy.json`, scenarios whose `screen:` is this one), and asks `RequestParity`.
public static class UiRequestCheck
{
    /// null when the page tests wrote no log for the screen (nothing can be judged).
    public static RequestFindings? Check(string integration, string screen, string specText, string? registry)
    {
        var dir = Path.Combine(integration, ".factory", "requests", screen);
        if (!Directory.Exists(dir)) return null;
        var logs = new List<LoggedRequest>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var body = e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? FSharpOption<string>.Some(b.GetString()!) : FSharpOption<string>.None;
                logs.Add(new LoggedRequest(e.GetProperty("method").GetString()!, e.GetProperty("path").GetString()!, e.GetProperty("matched").GetBoolean(), JsonShapes.ofBody(body)));
            }
        }
        var sources = new List<string>();
        using (var spec = JsonDocument.Parse(specText))
            if (spec.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                foreach (var d in data.EnumerateObject())
                    if (d.Value.ValueKind == JsonValueKind.Object && d.Value.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String) sources.Add(s.GetString()!);
        return RequestParity.compare(ListModule.OfSeq(logs), ListModule.OfSeq(sources), Legacy(registry, screen));
    }

    private static FSharpOption<FSharpList<LegacyRequest>> Legacy(string? registry, string screen)
    {
        if (registry is null) return FSharpOption<FSharpList<LegacyRequest>>.None;
        var scenarios = Path.Combine(registry, "parity", "scenarios");
        if (!Directory.Exists(scenarios)) return FSharpOption<FSharpList<LegacyRequest>>.None;
        var rows = new List<LegacyRequest>();
        var any = false;
        foreach (var y in Directory.EnumerateFiles(scenarios, "*.yaml"))
        {
            if (!Regex.IsMatch(File.ReadAllText(y), @"^screen:\s*['""]?" + Regex.Escape(screen) + @"['""]?\s*$", RegexOptions.Multiline)) continue;
            var legacy = Path.Combine(registry, "parity", "baselines", Path.GetFileNameWithoutExtension(y), "legacy.json");
            if (!File.Exists(legacy)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            if (!doc.RootElement.TryGetProperty("requests", out var reqs) || reqs.ValueKind != JsonValueKind.Array) continue; // not recorded
            any = true;
            foreach (var r in reqs.EnumerateArray())
            {
                var shape = r.TryGetProperty("request", out var q) && q.ValueKind != JsonValueKind.Null ? FSharpOption<JsonShape>.Some(JsonShapes.ofShapeElement(q)) : FSharpOption<JsonShape>.None;
                rows.Add(new LegacyRequest(r.GetProperty("method").GetString()!, r.GetProperty("path").GetString()!, shape));
            }
        }
        return any ? FSharpOption<FSharpList<LegacyRequest>>.Some(ListModule.OfSeq(rows)) : FSharpOption<FSharpList<LegacyRequest>>.None;
    }
}
