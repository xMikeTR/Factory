namespace Factory.Harness;

public sealed record PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow(string reason = "allowed") => new(true, reason);
    public static PolicyDecision Deny(string reason) => new(false, reason);
}

/// Evaluated before every tool call, in the agent middleware.
///
/// Target: an adapter over the AGT ACS runtime loading mlang-compiled manifests per role
/// (Phase 0 exit criterion in the plan). Until then RoleAllowListPolicy gives the same shape of
/// guarantee for tool names, and ScopedWorkspace enforces paths itself, so path safety does not
/// depend on the policy engine being wired.
public interface IToolPolicy
{
    PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args);
}

public static class Roles
{
    public const string Planner = "s2-planner";            // plan-level: handover -> plan.json
    public const string TaskPlanner = "s2-task-planner";    // per-task approach note, read-only
    public const string Executor = "s2-executor";           // writes code inside `writes`
}

public sealed class RoleAllowListPolicy : IToolPolicy
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [Roles.Planner] = ["read_file", "list_files"],
        [Roles.TaskPlanner] = ["read_file", "list_files", "raise_needs_decision"],
        [Roles.Executor] = ["read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision"],
    };

    public PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args) =>
        Allowed.TryGetValue(role, out var tools) && tools.Contains(toolName)
            ? PolicyDecision.Allow()
            : PolicyDecision.Deny($"role '{role}' may not call '{toolName}'");
}
