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
        if (!Directory.Exists(directory)) throw new FactoryRejectedException("tool policies are missing", [$"{directory} does not exist; set FACTORY_POLICY_DIR to the folder holding the role policy files"]);
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

    /// The engine has no substring operator, so facts a rule needs are computed here and passed in args (as the Jira gate does).
    private static Dictionary<string, object> DerivedArgs(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        var d = args.ToDictionary(kv => kv.Key, kv => kv.Value ?? (object)"");
        var skips = toolName == "run_command" && args.TryGetValue("command", out var c) && c?.ToString()?.Contains("--no-verify", StringComparison.Ordinal) == true;
        d["skips_hooks"] = skips ? "true" : "false";
        // UI contract facts: the same answers RoleAllowListPolicy and ScopedWorkspace get from UiPolicyFacts.
        string Text(string key) => args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        string Flag(bool b) => b ? "true" : "false";
        var command = toolName == "run_command" ? Text("command") : "";
        d["updates_snapshots"] = Flag(command != "" && UiPolicyFacts.updatesSnapshots(command));
        d["touches_ui_contract"] = Flag(command != "" && UiPolicyFacts.touchesUiContract(command));
        d["ui_protected_path"] = Flag(toolName is "write_file" or "delete_file" && Text("path") != "" && UiPolicyFacts.isUiProtected(Text("path")));
        d["reads_holdout"] = Flag((toolName == "read_file" && UiPolicyFacts.isHoldout(Text("path"))) || (toolName == "list_files" && UiPolicyFacts.isHoldout(Text("directory"))));
        return d;
    }

    public PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args)
    {
        if (!_roles.TryGetValue(role, out var p))
            return PolicyDecision.Deny($"role '{role}' may not call '{toolName}': no policy file for the role");

        var context = new Dictionary<string, object>
        {
            ["role"] = role,
            ["tool_name"] = toolName,
            ["args"] = DerivedArgs(toolName, args),
        };
        var d = p.Engine.Evaluate("did:mesh:" + role, context);
        var where = $"{p.File}{(string.IsNullOrEmpty(d.MatchedRule) ? "" : $" rule '{d.MatchedRule}'")} ({p.Source})";
        return d.Allowed
            ? PolicyDecision.Allow($"allowed by {where}")
            : PolicyDecision.Deny($"role '{role}' may not call '{toolName}': {d.Reason} [{where}]");
    }
}
