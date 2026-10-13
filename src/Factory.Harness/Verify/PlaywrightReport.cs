using System.Text.Json;

namespace Factory.Harness.Verify;

/// Reads Playwright's JSON reporter output (`--reporter=json`): which spec files had a failing spec,
/// and the retained trace of the first failure, if any.
public static class PlaywrightReport
{
    public sealed record Failure(string File, string? Trace);

    /// `frozenPaths` are the handover's spec paths: the reporter gives files relative to testDir, so a
    /// reported file is matched to the frozen path it is a suffix of.
    public static IReadOnlyList<Failure> Failures(string json, IReadOnlyCollection<string> frozenPaths)
    {
        var failures = new Dictionary<string, string?>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("suites", out var suites)) Walk(suites, null, failures);
        return failures
            .Select(f => new Failure(Resolve(f.Key, frozenPaths), f.Value))
            .OrderBy(f => f.File, StringComparer.Ordinal).ToList();
    }

    private static void Walk(JsonElement suites, string? file, Dictionary<string, string?> failures)
    {
        foreach (var suite in suites.EnumerateArray())
        {
            var f = suite.TryGetProperty("file", out var fe) && fe.ValueKind == JsonValueKind.String ? fe.GetString() : file;
            if (suite.TryGetProperty("specs", out var specs))
                foreach (var spec in specs.EnumerateArray())
                {
                    var ok = !spec.TryGetProperty("ok", out var okp) || okp.ValueKind != JsonValueKind.False;
                    var specFile = spec.TryGetProperty("file", out var sf) && sf.ValueKind == JsonValueKind.String ? sf.GetString() : f;
                    if (ok || specFile is null) continue;
                    failures.TryAdd(specFile, null);
                    failures[specFile] ??= TraceOf(spec);
                }
            if (suite.TryGetProperty("suites", out var nested)) Walk(nested, f, failures);
        }
    }

    private static string? TraceOf(JsonElement spec)
    {
        if (!spec.TryGetProperty("tests", out var tests)) return null;
        foreach (var test in tests.EnumerateArray())
            if (test.TryGetProperty("results", out var results))
                foreach (var result in results.EnumerateArray())
                    if (result.TryGetProperty("attachments", out var attachments))
                        foreach (var a in attachments.EnumerateArray())
                            if (a.TryGetProperty("name", out var n) && n.GetString() == "trace" && a.TryGetProperty("path", out var p))
                                return p.GetString();
        return null;
    }

    private static string Resolve(string reported, IReadOnlyCollection<string> frozenPaths)
    {
        var r = reported.Replace('\\', '/');
        return frozenPaths.FirstOrDefault(p => p == r)
            ?? frozenPaths.FirstOrDefault(p => p.EndsWith("/" + r, StringComparison.Ordinal))
            ?? r;
    }
}
