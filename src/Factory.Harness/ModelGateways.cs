using Factory.Core;
using Microsoft.Extensions.AI;

namespace Factory.Harness;

/// The two model pools: the one that writes (planner, task-planner, test author, implementer) and the
/// one that judges (reviewer, QA). Same-family reviews share the implementer's blind spots, so they are
/// allowed only as an explicit, stamped override (Core's `Independence`). Independence is an invariant checked here, at start-up, not a
/// per-run choice: Laya picks the tier on the reviewer pool, never the provider.
///
/// Reviewer settings are the implementer's FACTORY_* names with a REVIEWER_ infix, each falling back to
/// the implementer's value when unset
/// (FACTORY_REVIEWER_PROVIDER, _GATEWAY_URL, _GATEWAY_KEY, _MODEL_FRONTIER/STANDARD/SMALL). Families
/// are declared (FACTORY_MODEL_FAMILY, FACTORY_REVIEWER_MODEL_FAMILY); the Anthropic provider
/// defaults to "anthropic", anything else is unknown, and unknown never proves independence.
public sealed record ModelGateways(GatewayOptions Implementer, GatewayOptions Reviewer, Independence ReviewerIndependence)
{
    private static readonly string[] Settings =
        ["PROVIDER", "VERTEX_PROJECT", "VERTEX_REGION", "GATEWAY_URL", "GATEWAY_KEY", "MODEL_FRONTIER", "MODEL_STANDARD", "MODEL_SMALL", "MODEL_FAMILY"];

    public static ModelGateways FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""), mergesAreDryRun: false);

    public static ModelGateways FromEnvironment(IReadOnlyDictionary<string, string> env, bool mergesAreDryRun)
    {
        var implementer = GatewayOptions.FromEnvironment(env);
        var reviewerConfigured = Settings.Any(s => env.TryGetValue($"FACTORY_REVIEWER_{s}", out var v) && v.Length > 0);

        var reviewer = implementer;
        if (reviewerConfigured)
        {
            var remapped = env.ToDictionary(e => e.Key, e => e.Value); // unset reviewer settings fall back to the implementer's
            foreach (var s in Settings)
                if (env.TryGetValue($"FACTORY_REVIEWER_{s}", out var v) && v.Length > 0) remapped[$"FACTORY_{s}"] = v;
            // the one setting not inherited: a reviewer on another family is likely not Claude
            remapped["FACTORY_GATEWAY_CACHE_CONTROL"] = env.GetValueOrDefault("FACTORY_REVIEWER_GATEWAY_CACHE_CONTROL", "");
            reviewer = GatewayOptions.FromEnvironment(remapped);
        }

        var independence = FamilyOf(env, "FACTORY_MODEL_FAMILY", implementer) is { } a
            && FamilyOf(env, "FACTORY_REVIEWER_MODEL_FAMILY", reviewer) is { } b
            && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                ? Independence.Independent
                : Independence.SameFamily;

        if (independence == Independence.SameFamily)
        {
            var allowed = env.TryGetValue("FACTORY_REVIEWER_SAME_FAMILY", out var o) && o == "allow";
            if (!allowed)
                throw new FactoryRejectedException("the reviewer is not independent of the implementer",
                [
                    "configure the reviewer on another model family (FACTORY_REVIEWER_PROVIDER / _GATEWAY_URL / _GATEWAY_KEY, and declare FACTORY_MODEL_FAMILY and FACTORY_REVIEWER_MODEL_FAMILY),",
                    "or set FACTORY_REVIEWER_SAME_FAMILY=allow with dry-run merges (its reviews are stamped same-family)",
                ]);
            if (!mergesAreDryRun)
                throw new FactoryRejectedException("a same-family reviewer is allowed only while merges are dry-run",
                    ["unset FACTORY_REVIEWER_SAME_FAMILY or configure an independent reviewer before real merges"]);
        }

        return new ModelGateways(implementer, reviewer, independence);
    }

    private static string? FamilyOf(IReadOnlyDictionary<string, string> env, string name, GatewayOptions gateway) =>
        env.TryGetValue(name, out var v) && v.Length > 0 ? v
        : gateway.Provider is ModelProvider.Anthropic or ModelProvider.Vertex ? "anthropic" : null;

    public IChatClient ChatClientFor(string role, ModelTier tier) =>
        (role is Roles.Reviewer or Roles.Qa or Roles.UxJudge or Roles.Repro or Roles.Diagnosis ? Reviewer : Implementer).ChatClientFor(tier);
}
