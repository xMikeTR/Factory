using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Factory.Core;
using Microsoft.FSharp.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Registry;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// A handover frozen by Designc (handovers/&lt;id&gt;/): handover.yaml names elements.json and the bound
/// decisions and lists every file with its hash16. elements.json is Designc's resolved model (its
/// docs/handover-elements.schema.json), so the DSL is never parsed here; only elements that declare
/// code are code-backed and appear in the Handover.
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

        var elementsPath = Str(manifest, "elements");
        var resolved = elementsPath is null
            ? Fail("handover.yaml names no elements file; re-freeze it with a designc that writes elements.json")
            : files.TryGetValue(elementsPath, out var json) ? ParseElements(json, errors)
            : Fail($"handover.yaml: elements '{elementsPath}' is not among the frozen files");

        var elements = resolved is null ? [] : Elements(resolved.Value, errors);
        var byName = resolved?.Elements.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Id) ?? [];

        var decisions = new List<BoundDecision>();
        foreach (var did in List(manifest, "decisions").Select(d => d?.ToString()).OfType<string>())
        {
            var rel = files.Keys.FirstOrDefault(k => Regex.IsMatch(k, $@"^decisions/{Regex.Escape(did)}\.ya?ml$"));
            if (rel is null) { errors.Add($"decision {did} is listed but its file is not frozen"); continue; }
            decisions.Add(Decision(did, Yaml.Deserialize<Dictionary<string, object?>>(files[rel]) ?? [], byName, errors));
        }

        var vocabulary = resolved?.Events ?? [];

        var protectedPaths = Patterns(config.ProtectedPaths, "S2Config.ProtectedPaths", errors)
            .Concat(Patterns(resolved?.Reserved ?? [], "elements.json reserved", errors))
            .DistinctBy(p => p.Value).ToList();

        if (errors.Count > 0) Reject(handoverDir, errors);
        return new Handover(
            id!,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            Core.ToList(elements),
            Core.ToList(decisions),
            Core.ToList(protectedPaths),
            Core.ToList(vocabulary),
            Core.ToList(Relationships(resolved!.Value, elements)),
            Core.ToList(AcceptanceSpecs(manifest, errors)),
            Str(manifest, "work_item") is { } workItem ? FSharpOption<string>.Some(workItem) : FSharpOption<string>.None,
            Tokens(manifest, files, errors));

        ResolvedModel? Fail(string error) { errors.Add(error); return null; }
    }

    /// The frozen token file named by `tokens`, as text; absent when the handover names none.
    private static FSharpOption<string> Tokens(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        if (Str(manifest, "tokens") is not { } path) return FSharpOption<string>.None;
        if (files.TryGetValue(path, out var text)) return FSharpOption<string>.Some(text);
        errors.Add($"handover.yaml: tokens '{path}' is not among the frozen files");
        return FSharpOption<string>.None;
    }

    /// The `specs` paths with the hash `files` records for each (the integrity loop above already proved the text).
    private static List<AcceptanceSpec> AcceptanceSpecs(Dictionary<string, object?> manifest, List<string> errors)
    {
        var hashes = List(manifest, "files").OfType<Dictionary<object, object?>>()
            .Where(f => f.GetValueOrDefault("path") is string && f.GetValueOrDefault("hash") is not null)
            .ToDictionary(f => (string)f["path"]!, f => f["hash"]!.ToString()!, StringComparer.Ordinal);
        var specs = new List<AcceptanceSpec>();
        foreach (var path in List(manifest, "specs").OfType<string>())
        {
            if (hashes.TryGetValue(path, out var hash)) specs.Add(new AcceptanceSpec(path, hash));
            else errors.Add($"handover.yaml: spec '{path}' is not among the frozen files");
        }
        return specs;
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

    private sealed record ElementJson(string Id, string Name, string Kind, List<string>? Code, string? Risk, [property: JsonPropertyName("in_scope")] bool? InScope);
    private sealed record RelationshipJson(string From, string To);
    private sealed record ElementsJson(string? Schema, List<ElementJson>? Elements, List<RelationshipJson>? Relationships, List<string>? Events, List<string>? Reserved);
    private readonly record struct ResolvedModel(List<ElementJson> Elements, List<RelationshipJson> Relationships, List<string> Events, List<string> Reserved);

    /// /1 is the design handover; /2 is an item handover (adds `in_scope` per element and `reserved` paths).
    private static readonly string[] ElementsSchemas = ["designc.handover.elements/1", "designc.handover.elements/2"];

    private static ResolvedModel? ParseElements(string json, List<string> errors)
    {
        ElementsJson? doc;
        try { doc = JsonSerializer.Deserialize<ElementsJson>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { errors.Add($"elements.json: {ex.Message}"); return null; }
        if (doc?.Schema is null || !ElementsSchemas.Contains(doc.Schema)) { errors.Add($"elements.json: schema '{doc?.Schema}' is not {string.Join(" or ", ElementsSchemas)}"); return null; }
        return new ResolvedModel(doc.Elements ?? [], doc.Relationships ?? [], doc.Events ?? [], doc.Reserved ?? []);
    }

    /// The model's relationships between elements that own code (the others cannot be crossed by a task).
    private static List<Relationship> Relationships(ResolvedModel model, List<ElementSpec> owners)
    {
        var ids = owners.Select(o => o.Id.Value).ToHashSet();
        return model.Relationships
            .Where(r => ids.Contains(r.From) && ids.Contains(r.To))
            .Select(r => new Relationship(ElementId.NewElementId(r.From), ElementId.NewElementId(r.To)))
            .Distinct().ToList();
    }

    private static List<ElementSpec> Elements(ResolvedModel model, List<string> errors)
    {
        var specs = new List<ElementSpec>();
        foreach (var e in model.Elements)
        {
            if (e.Code is null) continue;
            var risk = e.Risk switch
            {
                "low" => RiskLevel.Low,
                "medium" => RiskLevel.Medium,
                "high" => RiskLevel.High,
                null => Fail<RiskLevel>($"element '{e.Name}' declares code but no risk"),
                var other => Fail<RiskLevel>($"element '{e.Name}': risk '{other}' is not low, medium or high"),
            };
            var areas = Patterns(e.Code, $"element '{e.Name}' code", errors);
            if (risk is not null) specs.Add(new ElementSpec(ElementId.NewElementId(e.Id), e.Name, Core.ToList(areas), risk, e.InScope ?? true));
        }
        return specs;

        T? Fail<T>(string error) where T : class { errors.Add(error); return null; }
    }

    private static BoundDecision Decision(string id, Dictionary<string, object?> yaml, Dictionary<string, string> byName, List<string> errors)
    {
        if (Str(yaml, "id") != id) errors.Add($"decision {id}: file says id '{Str(yaml, "id")}'");
        if (Str(yaml, "status") != "bound") errors.Add($"decision {id} is listed in the handover but is not bound");
        var binds = new List<ElementId>();
        foreach (var name in List(yaml, "binds").Select(b => b?.ToString()).OfType<string>())
        {
            if (byName.TryGetValue(name, out var el)) binds.Add(ElementId.NewElementId(el));
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
