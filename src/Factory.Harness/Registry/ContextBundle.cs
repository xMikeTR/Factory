using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Harness.Registry;

/// What S2 asks designc's core for: the context one role needs for one task (designc's bundle key).
/// Null Handover means "any": the bundle's own handover is accepted.
public sealed record BundleRequest(string? Handover, string? WorkItem, string? Task, string Role, IReadOnlyList<string> Elements);

/// A bound procedure as the bundle gives it; a trace names the ones given as {id, version, hash}.
public sealed record BundleProcedure(string Id, string Version, string Hash, string Title, IReadOnlyList<string> Steps);

/// A designc.context.bundle/1 that passed the shape and key checks. Json is the text as received, kept for the
/// bundle_id recompute (2b) and for the agent's context (2d).
public sealed partial record ContextBundle(
    string BundleId, string Role, string? Handover, IReadOnlyList<string> Elements,
    IReadOnlyList<BundleProcedure> Procedures, string Json)
{
    public const string Schema = "designc.context.bundle/1";

    /// Checks the fields S2 relies on, not the whole schema; anything wrong is Missed, never an exception.
    public static BundleFetch Parse(string json, BundleRequest request, string source)
    {
        BundleFetch Miss(string reason) => new BundleFetch.Missed(source, reason);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { return Miss($"not JSON: {e.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Miss("not a JSON object");
            if (Str(root, "schema") != Schema) return Miss($"schema is not {Schema}");
            var id = Str(root, "bundle_id");
            if (id is null || !BundleIdPattern().IsMatch(id)) return Miss("bundle_id is not sha256:<64 hex>");
            if (!root.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.Object) return Miss("key is missing");

            var role = Str(key, "role");
            if (role != request.Role) return Miss($"key role '{role}' is not the requested role '{request.Role}'");
            var handover = Str(key, "handover");
            if (request.Handover is not null && handover != request.Handover)
                return Miss($"key handover '{handover}' is not the requested handover '{request.Handover}'");
            var elements = Strings(key, "elements");
            if (elements is null) return Miss("key elements is not a list of strings");

            if (!root.TryGetProperty("procedures", out var procs) || procs.ValueKind != JsonValueKind.Array)
                return Miss("procedures is not a list");
            var procedures = new List<BundleProcedure>();
            foreach (var p in procs.EnumerateArray())
            {
                var (pid, version, hash, title, steps) = p.ValueKind == JsonValueKind.Object
                    ? (Str(p, "id"), Str(p, "version"), Str(p, "hash"), Str(p, "title"), Strings(p, "steps"))
                    : (null, null, null, null, null);
                if (pid is null || version is null || title is null || steps is null || hash is null || !Hash16Pattern().IsMatch(hash))
                    return Miss($"procedure {procedures.Count} needs id, version, a 16-hex hash, title and steps");
                procedures.Add(new BundleProcedure(pid, version, hash, title, steps));
            }
            var computed = ComputeId(root);
            if (computed != id) return Miss($"bundle_id does not match its content (computed {computed})");
            return new BundleFetch.Fetched(new ContextBundle(id, role!, handover, elements, procedures, json));
        }
    }

    /// designc's rule: "sha256:" + sha256 of the canonical JSON (keys sorted at every level, no whitespace) of every field but bundle_id.
    public static string ComputeId(JsonElement root)
    {
        var sb = new StringBuilder();
        Canonical(root, sb, skipBundleId: true);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // Matches JS: keys in UTF-16 code unit order, JSON.stringify string escaping, numbers as written.
    private static void Canonical(JsonElement v, StringBuilder sb, bool skipBundleId = false)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in v.EnumerateObject().Where(p => !(skipBundleId && p.Name == "bundle_id")).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Quote(p.Name, sb);
                    sb.Append(':');
                    Canonical(p.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var i = 0;
                foreach (var e in v.EnumerateArray()) { if (i++ > 0) sb.Append(','); Canonical(e, sb); }
                sb.Append(']');
                break;
            case JsonValueKind.String: Quote(v.GetString()!, sb); break;
            default: sb.Append(v.GetRawText()); break;
        }
    }

    private static void Quote(string s, StringBuilder sb)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    var lone = char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) && !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1]));
                    if (c < 0x20 || lone) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? Strings(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
            ? v.EnumerateArray().Select(e => e.GetString()!).ToList()
            : null;

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")] private static partial Regex BundleIdPattern();
    [GeneratedRegex("^[0-9a-f]{16}$")] private static partial Regex Hash16Pattern();
}

/// The result of asking for a bundle. Missed is not an error: the run continues without one (trace bundle_id null).
public abstract record BundleFetch
{
    private BundleFetch() { }
    public sealed record Fetched(ContextBundle Bundle) : BundleFetch;
    /// Source: "file", "designc" or "none".
    public sealed record Missed(string Source, string Reason) : BundleFetch;
}
