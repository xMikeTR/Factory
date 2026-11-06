using Factory.Core;
using Microsoft.FSharp.Collections;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// The model catalog the router picks from: `catalog/models.yaml`, shipped next to the binaries. Point
/// FACTORY_CATALOG at another file to use a different list (for example one for work). This file is the only place
/// model names live, so changing a model, or adding Gemini, is a row here and no code change.
///
///   models:
///     - id: vertex/claude-sonnet      # unique; turn ledger rows carry it as model_id
///       modelName: claude-sonnet-5-5  # what Vertex is called with
///       tier: standard                # small | standard | frontier
///       family: claude                # optional for claude-* names; the reviewer must differ from the implementer's
///       protocol: anthropicVertex     # optional: anthropicVertex for claude-*, else vertexOpenAICompatible
///       caps: [toolUse, structuredOutput]   # optional; also vision, longContext
///       contextWindow: 200000         # optional
///       approvedFor: [public, internal]     # optional; also regulated
///       prior: [1, 1]                 # optional pseudo-counts (successes, failures)
///       price: { in: 3, out: 15 }     # optional, per million tokens; placeholder until APIM prices are imported
///
/// Errors are collected and returned, never thrown.
public static class CatalogFile
{
    public sealed record CatalogResult(IReadOnlyList<ModelEntry> Models, IReadOnlyList<string> Errors);

    private sealed class Doc { public List<Row>? Models { get; set; } }

    private sealed class Row
    {
        public string? Id { get; set; }
        public string? Family { get; set; }
        public string? Protocol { get; set; }
        public string? ModelName { get; set; }
        public string? Tier { get; set; }
        public List<string>? Caps { get; set; }
        public int? ContextWindow { get; set; }
        public List<string>? ApprovedFor { get; set; }
        public List<double>? Prior { get; set; }
        public PriceRow? Price { get; set; }
        public string? Source { get; set; }
    }

    private sealed class PriceRow { public decimal? In { get; set; } public decimal? Out { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// The catalog that ships with the build.
    public static string ShippedPath() => Path.Combine(AppContext.BaseDirectory, "catalog", "models.yaml");

    /// FACTORY_CATALOG when set, else the shipped file.
    public static CatalogResult Load(IReadOnlyDictionary<string, string> env)
    {
        var path = env.TryGetValue("FACTORY_CATALOG", out var p) && p.Length > 0 ? p : ShippedPath();
        if (!File.Exists(path)) return new CatalogResult([], [$"catalog file not found: {path}"]);
        var r = Parse(File.ReadAllText(path));
        return r with { Errors = [.. r.Errors.Select(e => $"{path}: {e}")] };
    }

    public static CatalogResult Parse(string yaml)
    {
        Doc? doc;
        try { doc = Yaml.Deserialize<Doc?>(yaml); }
        catch (YamlException e) { return new CatalogResult([], [e.Message]); }
        var rows = doc?.Models ?? [];
        if (rows.Count == 0) return new CatalogResult([], ["no models listed (expected a `models:` list)"]);

        var errors = new List<string>();
        var models = new List<ModelEntry>();
        var seen = new HashSet<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var at = $"models[{i}]" + (string.IsNullOrWhiteSpace(row.Id) ? "" : $" ({row.Id})");
            var before = errors.Count;
            if (string.IsNullOrWhiteSpace(row.Id)) errors.Add($"{at}: id is required");
            else if (!seen.Add(row.Id)) errors.Add($"{at}: duplicate id '{row.Id}'");
            if (string.IsNullOrWhiteSpace(row.ModelName)) errors.Add($"{at}: modelName is required");
            var claude = row.ModelName is { } n && VertexAuthPolicy.IsClaude(n);

            var tier = Parse(row.Tier, at, "tier", errors, required: true, new Dictionary<string, ModelTier>(StringComparer.OrdinalIgnoreCase)
                { ["small"] = ModelTier.Small, ["standard"] = ModelTier.Standard, ["frontier"] = ModelTier.Frontier }, ModelTier.Small);
            var protocol = Parse(row.Protocol, at, "protocol", errors, required: false, new Dictionary<string, Protocol>(StringComparer.OrdinalIgnoreCase)
                { ["anthropicVertex"] = Protocol.AnthropicVertex, ["vertexOpenAICompatible"] = Protocol.VertexOpenAICompatible },
                claude ? Protocol.AnthropicVertex : Protocol.VertexOpenAICompatible);
            var caps = (row.Caps ?? ["toolUse", "structuredOutput"]).Select(c => Parse(c, at, "caps", errors, required: true,
                new Dictionary<string, Capability>(StringComparer.OrdinalIgnoreCase)
                { ["toolUse"] = Capability.ToolUse, ["vision"] = Capability.Vision, ["longContext"] = Capability.LongContext, ["structuredOutput"] = Capability.StructuredOutput }, Capability.ToolUse)).ToList();
            var approved = (row.ApprovedFor ?? ["public", "internal"]).Select(c => Parse(c, at, "approvedFor", errors, required: true,
                new Dictionary<string, DataClass>(StringComparer.OrdinalIgnoreCase)
                { ["public"] = DataClass.Public, ["internal"] = DataClass.Internal, ["regulated"] = DataClass.Regulated }, DataClass.Internal)).ToList();

            var family = string.IsNullOrWhiteSpace(row.Family) ? (claude ? "claude" : null) : row.Family;
            if (family is null) errors.Add($"{at}: family is required (only claude-* models default it)");
            var window = row.ContextWindow ?? 200_000;
            if (window <= 0) errors.Add($"{at}: contextWindow must be > 0");
            var priceIn = row.Price?.In ?? 0m;
            var priceOut = row.Price?.Out ?? 0m;
            if (priceIn < 0 || priceOut < 0) errors.Add($"{at}: price must not be negative");
            var prior = row.Prior ?? [1.0, 1.0];
            if (prior.Count != 2 || prior.Any(x => x < 0)) errors.Add($"{at}: prior must be two non-negative numbers [successes, failures]");

            if (errors.Count > before) continue;
            models.Add(new ModelEntry(row.Id!, family!, protocol, row.ModelName!, tier, new Price(priceIn, priceOut),
                SetModule.OfSeq(caps), window, SetModule.OfSeq(approved), Tuple.Create(prior[0], prior[1])));
        }
        return new CatalogResult(models, errors);
    }

    private static T Parse<T>(string? value, string at, string field, List<string> errors, bool required, Dictionary<string, T> map, T fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) errors.Add($"{at}: {field} is required");
            return fallback;
        }
        if (map.TryGetValue(value, out var v)) return v;
        errors.Add($"{at}: unknown {field} '{value}' (expected {string.Join(" | ", map.Keys)})");
        return fallback;
    }
}
