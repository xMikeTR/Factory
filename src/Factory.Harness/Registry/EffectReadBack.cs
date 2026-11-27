using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Harness.Registry;

/// The read-back of one journey's effect: `pass`, `fail` (a failure message to record) or `could_not_run` (a reason for stderr only).
public sealed record EffectResult(string Outcome, string? Message);

/// A journey's `proves.effect`, read back once after its runs through the app the Factory started (docs/feature-map-contract.md).
/// Read-only by construction: GET and HEAD only, no body is ever sent. Messages name the method, path, JSON path and expected kind,
/// never a value, because a row must hold no data.
///
/// The shape check is structural. Formats such as `Aa` or `dd/0d` in `text:Aa` / `date:dd/0d` are NOT compared: the leaf's kind
/// (`text:`, `date:` -> a JSON string; `num:` -> a number or a string; `bool` -> a boolean; `null` -> null; any other string -> a string)
/// is. designc's parity shaper, which compares formats, is Node; porting it is out of scope.
public static class EffectReadBack
{
    private static readonly Regex Param = new(@":([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// `:name` segments filled from `--param name=value`; the first missing name comes back instead of a path.
    public static (string? Path, string? Missing) FillPath(string path, IReadOnlyDictionary<string, string> parms)
    {
        string? missing = null;
        var filled = Param.Replace(path, m =>
        {
            if (parms.TryGetValue(m.Groups[1].Value, out var v)) return v;
            missing ??= m.Groups[1].Value;
            return m.Value;
        });
        return missing is null ? (filled, null) : (null, missing);
    }

    /// The path to request, or an early result when nothing may be requested (not read-only, or a param is missing).
    public static (string? Path, EffectResult? Early) Prepare(FeatureEffect e, IReadOnlyDictionary<string, string> parms)
    {
        if (!IsReadOnly(e.Method)) return (null, new EffectResult("fail", $"effect {e.Method.ToUpperInvariant()} {e.Path} is not read-only"));
        var (path, missing) = FillPath(e.Path, parms);
        return missing is null ? (path, null) : (null, new EffectResult("could_not_run", $"effect {e.Path} needs --param {missing}"));
    }

    public static bool IsReadOnly(string method) => method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    /// designc's expansion of a journey's steps for one run: the entry action replaces the `entry` marker, or goes first when there is none;
    /// with no entry points the marker is dropped.
    public static int ExpandedStepCount(FeatureJourney j, bool hasEntryPoints) =>
        hasEntryPoints ? (j.HasEntryMarker ? j.StepCount : j.StepCount + 1) : (j.HasEntryMarker ? j.StepCount - 1 : j.StepCount);

    /// An absent expected status means any 2xx.
    public static string? CheckStatus(string method, string path, int actual, int? expected)
    {
        var m = method.ToUpperInvariant();
        if (expected is { } want) return actual == want ? null : $"effect {m} {path}: status {actual}, expected {want}";
        return actual is >= 200 and < 300 ? null : $"effect {m} {path}: status {actual}, expected 2xx";
    }

    /// The first mismatch of the actual body against the expected shape, or null.
    public static string? CheckShape(string method, string path, JsonElement expected, JsonElement actual)
    {
        var problem = Walk(expected, actual, "body", isRoot: true);
        return problem is null ? null : $"effect {method.ToUpperInvariant()} {path}: {problem}";
    }

    public static EffectResult Judge(FeatureEffect e, string path, int status, JsonElement? body)
    {
        var bad = CheckStatus(e.Method, path, status, e.Status);
        if (bad is null && e.Shape is { } shape && !e.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            bad = body is { } b ? CheckShape(e.Method, path, shape, b) : $"effect {e.Method.ToUpperInvariant()} {path}: body is not JSON, expected {Expected(shape)}";
        return bad is null ? new EffectResult("pass", null) : new EffectResult("fail", bad);
    }

    /// One GET/HEAD at `<baseUrl><path>`, with the body (saved by the caller as evidence; it may hold data, so it never goes in a row).
    public static async Task<(EffectResult Result, string? Body)> ReadAsync(HttpClient http, string baseUrl, FeatureEffect e,
        IReadOnlyDictionary<string, string> parms, CancellationToken ct)
    {
        var (path, early) = Prepare(e, parms);
        if (early is not null) return (early, null);
        var method = e.Method.ToUpperInvariant();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(new HttpMethod(method), baseUrl.TrimEnd('/') + path);
            using var response = await http.SendAsync(request, cts.Token);
            var text = method == "HEAD" ? null : await response.Content.ReadAsStringAsync(cts.Token);
            JsonElement? body = null;
            if (text is { Length: > 0 })
                try { body = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
            return (Judge(e, path!, (int)response.StatusCode, body), text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (new EffectResult("fail", $"effect {method} {path}: no answer ({(ex is HttpRequestException ? "request failed" : "timed out after 10s")})"), null);
        }
    }

    private static string? Walk(JsonElement expected, JsonElement actual, string at, bool isRoot)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                if (actual.ValueKind != JsonValueKind.Object) return Mismatch(at, actual, "object");
                foreach (var p in expected.EnumerateObject())
                {
                    var child = isRoot ? p.Name : $"{at}.{p.Name}";
                    if (!actual.TryGetProperty(p.Name, out var a)) return $"{child} is missing";
                    if (Walk(p.Value, a, child, false) is { } problem) return problem;
                }
                return null;
            case JsonValueKind.Array:
                if (actual.ValueKind != JsonValueKind.Array) return Mismatch(at, actual, "array");
                if (expected.GetArrayLength() == 0) return null;
                if (actual.GetArrayLength() == 0) return $"{at} is empty";
                return Walk(expected[0], actual[0], $"{at}[0]", false);
            default:
                var kind = LeafKind(expected);
                return Matches(kind, actual) ? null : Mismatch(at, actual, kind);
        }
    }

    private static string LeafKind(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => e.GetString() switch
        {
            { } s when s.StartsWith("text:") => "text",
            { } s when s.StartsWith("date:") => "date",
            { } s when s.StartsWith("num:") => "number or string",
            "bool" => "boolean",
            _ => "string",
        },
    };

    private static bool Matches(string kind, JsonElement a) => kind switch
    {
        "null" => a.ValueKind == JsonValueKind.Null,
        "number" => a.ValueKind == JsonValueKind.Number,
        "boolean" => a.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number or string" => a.ValueKind is JsonValueKind.Number or JsonValueKind.String,
        _ => a.ValueKind == JsonValueKind.String,
    };

    private static string Mismatch(string at, JsonElement actual, string expected) => $"{at} is {Describe(actual)}, expected {expected}";

    private static string Expected(JsonElement e) => e.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.Array => "array", _ => LeafKind(e) };

    private static string Describe(JsonElement a) => a.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };
}
