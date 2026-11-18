using System.Text.Json;

namespace Factory.Integration.Tests.Support;

/// A port of designc's ledger.py validate() over a copy of its generated ledger_schema.json
/// (Fixtures/designc). Same checks, same messages, so a row that passes here is one designc accepts.
public static class DesigncSchema
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "ledger_schema.json"))).RootElement;

    /// A top-level list of the pinned schema (`caught_by`, `origins`, `escape_buckets`).
    public static List<string> StringList(string key) => Strings(Schema.GetProperty(key));

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
            else if (rule == "usage_shape" && ev.TryGetProperty("usage", out var usage) && !UsageOk(usage))
                errors.Add($"{kind}.usage: must be a list of {{model, turns, tokens_in, tokens_out}}");
            else if (rule == "by_role_shape" && ev.TryGetProperty("by_role", out var byRole) && !ByRoleOk(byRole))
                errors.Add($"{kind}.by_role: must be a list of {{role, turns, tokens_in, tokens_out}}");
            else if (rule == "cause_shape" && CauseError(kind, ev) is { } causeError)
                errors.Add($"{kind}.{causeError}");
            else if (rule == "trace_shape" && !TraceOk(ev))
                errors.Add($"{kind}: models must be non-empty strings, procedures {{id, version, hash}}, tokens whole numbers >= 0");
            else if (rule == "ref_shape" && ev.TryGetProperty("ref", out var reference) && !RefOk(kind, reference))
                errors.Add($"{kind}.ref: must be {{kind, id}} with a kind this row allows");
            else if (rule == "caught_by_member" && MemberError(ev, "caught_by", "caught_by") is { } cb) errors.Add($"{kind}.{cb}");
            else if (rule == "origin_member" && MemberError(ev, "origin", "origins") is { } og) errors.Add($"{kind}.{og}");
            else if (rule == "bucket_member" && MemberError(ev, "bucket", "escape_buckets") is { } bk) errors.Add($"{kind}.{bk}");
            else if (rule == "verdict_shape" && VerdictError(ev) is { } vd) errors.Add($"{kind}.{vd}");
        return errors;
    }

    /// designc registry.py _valid(): what the gate loader keeps. Stricter than the schema for the
    /// kinds the gate reads; a row it rejects is silently ignored by the gate.
    private static readonly HashSet<string> GateEnvelope = ["schema_version", "event_id", "ts", "kind", "by", "system", "session", "task", "element", "payload"];
    private static readonly string[] GateKinds = ["check", "fitness", "eval", "needs_decision", "needs_decision_resolved"];

    public static bool KeptByGate(string line)
    {
        if (Validate(line).Count > 0) return false;
        var ev = JsonDocument.Parse(line).RootElement;
        var kind = ev.GetProperty("kind").GetString()!;
        if (!GateKinds.Contains(kind)) return true;
        var spec = Schema.GetProperty("kinds").GetProperty(kind);
        var known = new HashSet<string>(GateEnvelope);
        known.UnionWith(Strings(spec.GetProperty("required")));
        known.UnionWith(Strings(spec.GetProperty("optional")));
        return ev.EnumerateObject().All(p => known.Contains(p.Name));
    }

    /// designc ledger.js usage_shape: a list of {model, tier?, turns, tokens_in, tokens_out, cost?, cache_read?, cache_write?},
    /// the cache counts part of tokens_in.
    private static bool UsageOk(JsonElement usage) =>
        usage.ValueKind == JsonValueKind.Array && usage.EnumerateArray().All(u =>
            u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && m.GetString()!.Length > 0
            && (!u.TryGetProperty("tier", out var t) || t.ValueKind == JsonValueKind.String)
            && new[] { "turns", "tokens_in", "tokens_out" }.All(k => u.TryGetProperty(k, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var w) && w >= 0)
            && (!u.TryGetProperty("cost", out var c) || c.ValueKind == JsonValueKind.Number)
            && new[] { "cache_read", "cache_write" }.All(k => !u.TryGetProperty(k, out var n) || n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var w) && w >= 0)
            && Count(u, "cache_read") + Count(u, "cache_write") <= Count(u, "tokens_in"));

    /// designc ledger.js by_role_shape: a list of {role, turns, tokens_in, tokens_out}.
    private static bool ByRoleOk(JsonElement byRole) =>
        byRole.ValueKind == JsonValueKind.Array && byRole.EnumerateArray().All(u =>
            u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String && r.GetString()!.Length > 0
            && new[] { "turns", "tokens_in", "tokens_out" }.All(k => u.TryGetProperty(k, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var w) && w >= 0));

    /// ledger.py cause_shape: the cause's spelling, and the status (a trace's outcome) it needs.
    private static string? CauseError(string kind, JsonElement ev)
    {
        if (!ev.TryGetProperty("cause", out var c)) return null;
        var status = ev.TryGetProperty(kind == "trace" ? "outcome" : "status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        var cause = c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(cause, "^(stalled|abandoned|parked|gate_failed:[a-z0-9_.-]+)$")) return "cause: not a cause";
        if (cause is "stalled" or "abandoned" && status != "failed") return $"cause: {cause} needs status failed";
        if (cause == "parked" && status != "blocked") return "cause: parked needs status blocked";
        if (cause.StartsWith("gate_failed:") && status is not ("failed" or "blocked")) return "cause: gate_failed needs status failed or blocked";
        return null;
    }

    private static bool TraceOk(JsonElement ev)
    {
        static bool Text(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0;
        if (ev.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array
            && !ms.EnumerateArray().All(m => m.ValueKind == JsonValueKind.String && m.GetString()!.Length > 0)) return false;
        if (ev.TryGetProperty("procedures", out var ps) && ps.ValueKind == JsonValueKind.Array
            && !ps.EnumerateArray().All(p => p.ValueKind == JsonValueKind.Object && Text(p, "id") && Text(p, "version") && Text(p, "hash"))) return false;
        return new[] { "tokens_in", "tokens_out" }.All(k => !ev.TryGetProperty(k, out var n) || n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var w) && w >= 0);
    }

    /// ledger.py caught_by_member / origin_member / bucket_member: null or one of the schema's list.
    private static string? MemberError(JsonElement ev, string field, string listKey)
    {
        if (!ev.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        var allowed = StringList(listKey);
        return v.ValueKind == JsonValueKind.String && allowed.Contains(v.GetString()!) ? null : $"{field}: must be null or one of {string.Join('|', allowed)}";
    }

    /// ledger.py verdict_shape: change_ts not after ts; round only on review, a whole number >= 1; failures
    /// are objects of string values under state, kind, placement and criterion only (never detail text).
    private static string? VerdictError(JsonElement ev)
    {
        static bool Num(JsonElement e, string k, out double d) { d = 0; return e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d); }
        if (Num(ev, "change_ts", out var ct) && Num(ev, "ts", out var ts) && ct > ts) return "change_ts: must not be later than ts";
        if (ev.TryGetProperty("round", out var round))
        {
            if (!(ev.TryGetProperty("stage", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "review")) return "round: allowed only when stage is 'review'";
            if (!(Num(ev, "round", out var r) && Math.Floor(r) == r && r >= 1)) return "round: must be a whole number of at least 1";
        }
        if (ev.TryGetProperty("failures", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var f in fs.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) return $"failures[{i}]: must be an object";
                foreach (var p in f.EnumerateObject())
                {
                    if (p.Name is not ("state" or "kind" or "placement" or "criterion")) return $"failures[{i}]: only state, kind, placement and criterion are allowed (got '{p.Name}')";
                    if (p.Value.ValueKind != JsonValueKind.String) return $"failures[{i}].{p.Name}: must be a string";
                }
                i++;
            }
        }
        return null;
    }

    private static bool RefOk(string kind, JsonElement r)
    {
        string[] kinds = kind switch { "outcome" => ["ci", "qa", "parity", "integration"], "escape" => ["jira"], _ => [] };
        return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && kinds.Contains(k.GetString())
            && r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString()!.Length > 0;
    }

    private static long Count(JsonElement u, string k) => u.TryGetProperty(k, out var n) && n.TryGetInt64(out var w) ? w : 0;

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToList();
}
