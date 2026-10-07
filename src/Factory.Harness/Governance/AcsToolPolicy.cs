using AgentGovernance.Policy;
using Factory.Core;
using AgtPolicy = AgentGovernance.Policy.Policy;

namespace Factory.Harness.Governance;

/// IToolPolicy over the AGT policy engine: one policy file per role, `<dir>/<stem>.yaml` governs
/// role `s2-<stem>`. Every file must say where it came from (`# source: hand-authored` or
/// `# source: mlang@<version>`) and default to deny, so a missing rule fails closed.
/// An unknown role has no file and is denied.
public sealed class AcsToolPolicy : IToolPolicy
{
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "policies", "s2");

    private sealed record RolePolicy(PolicyEngine Engine, string File, string Source);

    private readonly IReadOnlyDictionary<string, RolePolicy> _roles;

    private AcsToolPolicy(IReadOnlyDictionary<string, RolePolicy> roles) => _roles = roles;

    public IEnumerable<string> Roles => _roles.Keys;

    public static AcsToolPolicy Load(string directory)
    {
        var roles = new Dictionary<string, RolePolicy>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.yaml").Order())
        {
            var file = Path.GetFileName(path);
            var role = "s2-" + Path.GetFileNameWithoutExtension(path);
            var yaml = File.ReadAllText(path);
            var source = yaml.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("# source:", StringComparison.Ordinal))?["# source:".Length..].Trim();
            if (string.IsNullOrEmpty(source)) { errors.Add($"{file}: no '# source:' header (hand-authored | mlang@<version>)"); continue; }

            AgtPolicy policy;
            try { policy = AgtPolicy.FromYaml(yaml); }
            catch (Exception e) { errors.Add($"{file}: {e.Message}"); continue; }
            if (policy.Name != role) { errors.Add($"{file}: name '{policy.Name}' does not match its file (expected '{role}')"); continue; }
            if (policy.DefaultAction != PolicyAction.Deny) { errors.Add($"{file}: default_action must be deny"); continue; }

            var engine = new PolicyEngine();
            engine.LoadPolicy(policy);
            roles[role] = new RolePolicy(engine, file, source);
        }
        if (errors.Count > 0) throw new FactoryRejectedException($"tool policies in {directory} are invalid", errors.ToArray());
        return new AcsToolPolicy(roles);
    }

    public PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args)
    {
        if (!_roles.TryGetValue(role, out var p))
            return PolicyDecision.Deny($"role '{role}' may not call '{toolName}': no policy file for the role");

        var context = new Dictionary<string, object>
        {
            ["role"] = role,
            ["tool_name"] = toolName,
            ["args"] = args.ToDictionary(kv => kv.Key, kv => kv.Value ?? (object)""),
        };
        var d = p.Engine.Evaluate("did:mesh:" + role, context);
        var where = $"{p.File}{(string.IsNullOrEmpty(d.MatchedRule) ? "" : $" rule '{d.MatchedRule}'")} ({p.Source})";
        return d.Allowed
            ? PolicyDecision.Allow($"allowed by {where}")
            : PolicyDecision.Deny($"role '{role}' may not call '{toolName}': {d.Reason} [{where}]");
    }
}
