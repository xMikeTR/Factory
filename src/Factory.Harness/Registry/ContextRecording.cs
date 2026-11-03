using System.Text.Json;

namespace Factory.Harness.Registry;

/// How a run asks for a task's context bundle and records the answer on the task's `context` ledger row.
public static class ContextRecording
{
    /// The bundle role for the phase holding the pen (opaque to S2; designc's bundle key).
    public static string Role(bool testAuthor) => testAuthor ? "test-author" : "implementer";

    /// context ledger payload: {role, source, bundle_id, procedures:[{id,version,hash}]} or {role, source, missed}.
    public static string Payload(string role, BundleFetch fetch) => fetch switch
    {
        BundleFetch.Fetched f => JsonSerializer.Serialize(new
        {
            role,
            source = f.Bundle.Source,
            bundle_id = f.Bundle.BundleId,
            procedures = f.Bundle.Procedures.Select(p => new { id = p.Id, version = p.Version, hash = p.Hash }),
        }),
        BundleFetch.Missed m => JsonSerializer.Serialize(new { role, source = m.Source, missed = m.Reason }),
        _ => throw new ArgumentOutOfRangeException(nameof(fetch)),
    };

    /// FACTORY_CONTEXT_INJECT=1 puts a fetched bundle into the agent's brief; off by default, so a bundle can be recorded
    /// on the trace for a while before it changes what the agent reads.
    public static bool Inject(string? setting) => setting == "1";

    /// FACTORY_CONTEXT_SOURCE: none (default) | file (FACTORY_CONTEXT_DIR, a folder holding bundles/<role>.json) |
    /// designc (FACTORY_DESIGNC_CORE, the path of designc-core.js). A kind without its setting is refused, not ignored.
    public static IContextSource Source(string? kind, string? dir, string? script, string registry) => kind switch
    {
        null or "" or "none" => new NoContextSource(),
        "file" => dir is { Length: > 0 } ? new FileContextSource(dir)
            : throw new ArgumentException("FACTORY_CONTEXT_SOURCE=file needs FACTORY_CONTEXT_DIR"),
        "designc" => script is { Length: > 0 } ? new DesigncCliContextSource(script, registry, "node", TimeSpan.FromSeconds(10))
            : throw new ArgumentException("FACTORY_CONTEXT_SOURCE=designc needs FACTORY_DESIGNC_CORE"),
        var other => throw new ArgumentException($"FACTORY_CONTEXT_SOURCE: '{other}' is not none|file|designc"),
    };
}
