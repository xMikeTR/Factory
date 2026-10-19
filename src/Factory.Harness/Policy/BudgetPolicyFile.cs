using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// Reads the `budget:` section of the target repo's .factory/policy.yaml (the same protected file as the merge,
/// quality and npm policy, read from the base checkout):
///
///   budget:
///     itemTokens: 2000000        # default limit per work item; absent = no limit
///     items:
///       W-BIG: 5000000           # per-item override
///
/// A missing section means no limit anywhere, so nothing is reported. A negative limit is rejected.
public static class BudgetPolicyFile
{
    private sealed class Doc { public Section? Budget { get; set; } }

    private sealed class Section
    {
        public long? ItemTokens { get; set; }
        public Dictionary<string, long>? Items { get; set; }
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static BudgetPolicy Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var b = PolicyYaml.Read<Doc>(Yaml, path)?.Budget ?? new Section();
        var errors = new List<string>();
        if (b.ItemTokens is < 0) errors.Add("budget.itemTokens: must be >= 0");
        errors.AddRange((b.Items ?? []).Where(kv => kv.Value < 0).Select(kv => $"budget.items.{kv.Key}: must be >= 0"));
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new BudgetPolicy(
            b.ItemTokens is { } d ? FSharpOption<long>.Some(d) : FSharpOption<long>.None,
            MapModule.OfSeq((b.Items ?? []).Select(kv => Tuple.Create(kv.Key, kv.Value))));
    }
}
