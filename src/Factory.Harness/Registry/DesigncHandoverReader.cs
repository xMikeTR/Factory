using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Registry;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// A handover frozen by Designc (handovers/&lt;id&gt;/): handover.yaml lists the model, the bound
/// decisions and every file with its hash16. Elements come from the model's d.code / d.risk
/// properties; only elements that declare d.code are code-backed and appear in the Handover.
public sealed class DesigncHandoverReader : IHandoverSource
{
    public async Task<Handover> LoadAsync(string handoverDir, S2Config config, CancellationToken ct)
    {
        var errors = new List<string>();
        var manifestPath = Path.Combine(handoverDir, "handover.yaml");
        if (!File.Exists(manifestPath)) Reject(handoverDir, ["handover.yaml is missing"]);

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        var manifest = Yaml.Deserialize<Dictionary<string, object?>>(Encoding.UTF8.GetString(manifestBytes)) ?? [];

        var id = Str(manifest, "id");
        if (id is null) errors.Add("handover.yaml: id is required");

        // Integrity: every frozen file must still hash to what Designc recorded.
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in List(manifest, "files").OfType<Dictionary<object, object?>>())
        {
            var rel = entry.GetValueOrDefault("path") as string;
            var hash = entry.GetValueOrDefault("hash")?.ToString();
            if (rel is null || hash is null) { errors.Add("handover.yaml: every file needs path and hash"); continue; }
            var full = Path.Combine(handoverDir, rel);
            if (!File.Exists(full)) { errors.Add($"{rel} is listed in handover.yaml but missing"); continue; }
            var text = await File.ReadAllTextAsync(full, ct);
            if (Hash16(text) != hash) errors.Add($"{rel} does not match its frozen hash (changed after the handover was frozen)");
            files[rel] = text;
        }

        var modelPath = Str(manifest, "model");
        var model = modelPath is not null && files.TryGetValue(modelPath, out var dsl)
            ? DslModel.Parse(dsl, errors)
            : null;
        if (model is null) errors.Add($"handover.yaml: model '{modelPath}' is not among the frozen files");

        var elements = model is null ? [] : Elements(model, errors);
        var byName = model?.Elements.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First()) ?? [];

        var decisions = new List<BoundDecision>();
        foreach (var did in List(manifest, "decisions").Select(d => d?.ToString()).OfType<string>())
        {
            var rel = files.Keys.FirstOrDefault(k => Regex.IsMatch(k, $@"^decisions/{Regex.Escape(did)}\.ya?ml$"));
            if (rel is null) { errors.Add($"decision {did} is listed but its file is not frozen"); continue; }
            decisions.Add(Decision(did, Yaml.Deserialize<Dictionary<string, object?>>(files[rel]) ?? [], byName, errors));
        }

        var vocabulary = files.Where(f => f.Key.StartsWith("behaviour/") && f.Key.EndsWith(".p")).OrderBy(f => f.Key)
            .SelectMany(f => Regex.Matches(f.Value, @"\bannounce\s+(\w+)").Select(m => m.Groups[1].Value))
            .Distinct().ToList();

        var protectedPaths = Patterns(config.ProtectedPaths, "S2Config.ProtectedPaths", errors);

        if (errors.Count > 0) Reject(handoverDir, errors);
        return new Handover(
            id!,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            Core.ToList(elements),
            Core.ToList(decisions),
            Core.ToList(protectedPaths),
            Core.ToList(vocabulary));
    }

    /// How a decision's check runs, from its command: `p check` is the P checker, `designc check`
    /// is the model lint, anything else is a script.
    public static string CheckKind(string run) =>
        run.TrimStart() switch
        {
            var r when r.StartsWith("p check", StringComparison.Ordinal) => "p",
            var r when r.StartsWith("designc check", StringComparison.Ordinal) => "lint",
            _ => "script",
        };

    /// Designc's hash16 (rules.py / core.js): two 32-bit FNV-1a passes over the UTF-16 code units.
    public static string Hash16(string s) => Fnv(s, 2166136261u) + Fnv(s, 2654435769u);

    private static string Fnv(string s, uint seed)
    {
        var h = seed;
        foreach (var unit in s) { h ^= unit; h *= 16777619u; }
        return h.ToString("x8");
    }

    private static List<ElementSpec> Elements(DslModel model, List<string> errors)
    {
        var specs = new List<ElementSpec>();
        foreach (var e in model.Elements)
        {
            if (!e.Properties.TryGetValue("d.code", out var code)) continue;
            var risk = e.Properties.GetValueOrDefault("d.risk") switch
            {
                "low" => RiskLevel.Low,
                "medium" => RiskLevel.Medium,
                "high" => RiskLevel.High,
                null => Fail<RiskLevel>($"element '{e.Name}' declares d.code but no d.risk"),
                var other => Fail<RiskLevel>($"element '{e.Name}': d.risk '{other}' is not low, medium or high"),
            };
            var areas = Patterns(code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), $"element '{e.Name}' d.code", errors);
            if (risk is not null) specs.Add(new ElementSpec(ElementId.NewElementId(e.Path), e.Name, Core.ToList(areas), risk));
        }
        return specs;

        T? Fail<T>(string error) where T : class { errors.Add(error); return null; }
    }

    private static BoundDecision Decision(string id, Dictionary<string, object?> yaml, Dictionary<string, DslElement> byName, List<string> errors)
    {
        if (Str(yaml, "id") != id) errors.Add($"decision {id}: file says id '{Str(yaml, "id")}'");
        if (Str(yaml, "status") != "bound") errors.Add($"decision {id} is listed in the handover but is not bound");
        var binds = new List<ElementId>();
        foreach (var name in List(yaml, "binds").Select(b => b?.ToString()).OfType<string>())
        {
            if (byName.TryGetValue(name, out var el)) binds.Add(ElementId.NewElementId(el.Path));
            else errors.Add($"{id} binds '{name}', which is not an element in the model");
        }
        var checks = List(yaml, "checks").Select(c => c?.ToString()).OfType<string>()
            .Where(c => !c.StartsWith("TODO", StringComparison.Ordinal))
            .Select(c => new DecisionCheck(CheckKind(c), c));
        return new BoundDecision(DecisionId.NewDecisionId(id), (Str(yaml, "statement") ?? "").Trim(), Core.ToList(binds), Core.ToList(checks));
    }

    private static List<PathPattern> Patterns(IEnumerable<string> raw, string where, List<string> errors)
    {
        var list = new List<PathPattern>();
        foreach (var r in raw)
        {
            var parsed = PathPatternModule.create(r);
            if (parsed.IsOk) list.Add(parsed.ResultValue);
            else errors.Add($"{where}: {parsed.ErrorValue}");
        }
        return list;
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();
    private static string? Str(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key)?.ToString();
    private static IEnumerable<object?> List(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) as List<object?> ?? [];

    private static void Reject(string dir, IEnumerable<string> errors) =>
        throw new FactoryRejectedException($"handover {Path.GetFileName(Path.TrimEndingDirectorySeparator(dir))} is malformed", errors.ToArray());
}
