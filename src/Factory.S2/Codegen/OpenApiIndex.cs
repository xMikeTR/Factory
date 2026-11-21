using YamlDotNet.Serialization;

namespace Factory.S2;

/// What an OpenAPI text declares, for the codegen manifest: operations (sorted by path, then method) and schema names (sorted ordinally).
/// Tolerant by design: anything that is not a mapping, or does not parse, gives empty lists and never throws.
public static class OpenApiIndex
{
    public sealed record Operation(string? Id, string Method, string Path);
    public sealed record Index(IReadOnlyList<Operation> Operations, IReadOnlyList<string> Schemas);

    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch", "head", "options"];
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static Index Read(string text)
    {
        object? doc;
        try { doc = Yaml.Deserialize<object>(text); }
        catch (Exception) { return new Index([], []); }
        if (doc is not IDictionary<object, object> root) return new Index([], []);

        var ops = new List<Operation>();
        if (Child(root, "paths") is { } paths)
            foreach (var (p, item) in paths)
                if (p?.ToString() is { } path && item is IDictionary<object, object> methods)
                    foreach (var m in Methods)
                        if (Child(methods, m) is { } op)
                            ops.Add(new Operation(op.TryGetValue("operationId", out var id) ? id?.ToString() : null, m.ToUpperInvariant(), path));
        var schemas = Child(root, "components") is { } c && Child(c, "schemas") is { } s
            ? s.Keys.Select(k => k?.ToString()).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
        return new Index(
            [.. ops.OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Method, StringComparer.Ordinal)], schemas);
    }

    private static IDictionary<object, object>? Child(IDictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var v) ? v as IDictionary<object, object> : null;
}
