using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Harness.Policy;

namespace Factory.Harness.Registry;

/// The versioned procedures a run used: its prompts, the S2 tool and gate policies, and the target repo's
/// .factory/policy.yaml. The hash is the identity (a changed file is a new procedure); version is the file's own
/// top-level `version:` where it declares one, else "none". Written on run_started so every trace names what ran it.
public static partial class Procedures
{
    public static IReadOnlyList<ProcedureRef> Fingerprint(string baseDirectory, string repoPath)
    {
        var refs = new List<ProcedureRef>();
        foreach (var f in Files(Path.Combine(baseDirectory, "prompts"), "*.md")) refs.Add(Ref("prompt:" + Path.GetFileNameWithoutExtension(f), f));
        foreach (var f in Files(Path.Combine(baseDirectory, "policies", "s2"), "*.yaml")) refs.Add(Ref("policy:" + Path.GetFileNameWithoutExtension(f), f));
        var target = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        if (File.Exists(target)) refs.Add(Ref("target:policy", target));
        return refs.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// run_started payload: {"procedures":[{id, version, hash}]}, what TraceReader reads back.
    public static string RunStartedPayload(IReadOnlyList<ProcedureRef> refs) =>
        JsonSerializer.Serialize(new { procedures = refs.Select(r => new { id = r.Id, version = r.Version, hash = r.Hash }) });

    private static IEnumerable<string> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : [];

    private static ProcedureRef Ref(string id, string path)
    {
        var bytes = File.ReadAllBytes(path);
        var version = VersionLine().Match(System.Text.Encoding.UTF8.GetString(bytes));
        return new ProcedureRef(id, version.Success ? version.Groups[1].Value : "none", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [GeneratedRegex("""^version:\s*["']?([^"'\s#]+)""", RegexOptions.Multiline)]
    private static partial Regex VersionLine();
}
