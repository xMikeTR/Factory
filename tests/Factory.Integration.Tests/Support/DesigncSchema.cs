using System.Text.Json;

namespace Factory.Integration.Tests.Support;

/// A port of designc's ledger.py validate() over a copy of its generated ledger_schema.json
/// (Fixtures/designc). Same checks, same messages, so a row that passes here is one designc accepts.
public static class DesigncSchema
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "ledger_schema.json"))).RootElement;

    public static string StreamOf(string kind) => Schema.GetProperty("kinds").GetProperty(kind).GetProperty("stream").GetString()!;

    public static List<string> Validate(string line)
    {
        var ev = JsonDocument.Parse(line).RootElement;
        if (ev.ValueKind != JsonValueKind.Object) return ["event must be an object"];
        var kind = ev.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : null;
        if (kind is null || !Schema.GetProperty("kinds").TryGetProperty(kind, out var spec)) return [$"kind: unknown kind '{kind}'"];

        var errors = new List<string>();
        if (!ev.TryGetProperty("by", out var by) || by.ValueKind != JsonValueKind.String || by.GetString() == "") errors.Add("by: required string");
        if (ev.TryGetProperty("schema_version", out var sv) && sv.GetInt32() != Schema.GetProperty("schema_version").GetInt32())
            errors.Add("schema_version: wrong version");
        if (ev.TryGetProperty("ts", out var ts) && ts.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) errors.Add("ts: must be a number (epoch ms)");
        if (ev.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Object) errors.Add("payload: must be an object");

        var known = new HashSet<string> { "schema_version", "event_id", "ts", "kind", "by" };
        known.UnionWith(Strings(Schema.GetProperty("envelope_optional")));
        known.UnionWith(Strings(spec.GetProperty("required")));
        known.UnionWith(Strings(spec.GetProperty("optional")));
        foreach (var prop in ev.EnumerateObject())
        {
            if (prop.Name.StartsWith('_')) errors.Add($"{prop.Name}: '_'-prefixed keys are reserved");
            else if (!known.Contains(prop.Name)) errors.Add($"{prop.Name}: unknown field for kind '{kind}' (put extra data under payload)");
        }
        foreach (var r in Strings(spec.GetProperty("required")))
            if (!ev.TryGetProperty(r, out _)) errors.Add($"{kind}.{r}: required");
        foreach (var e in spec.GetProperty("enums").EnumerateObject())
            if (ev.TryGetProperty(e.Name, out var v) && !Strings(e.Value).Contains(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()))
                errors.Add($"{kind}.{e.Name}: must be one of {string.Join('|', Strings(e.Value))}");
        foreach (var rule in Strings(spec.GetProperty("rules")))
            if (rule == "fail_needs_evidence" && ev.TryGetProperty("outcome", out var o) && o.GetString() == "fail"
                && !ev.TryGetProperty("exit_code", out _) && !(ev.TryGetProperty("reason", out var why) && why.GetString() is { Length: > 0 }))
                errors.Add($"{kind}.exit_code: required when outcome is 'fail' (or give reason)");
        return errors;
    }

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToList();
}
