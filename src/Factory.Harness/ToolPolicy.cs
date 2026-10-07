namespace Factory.Harness;

public sealed record PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow(string reason = "allowed") => new(true, reason);
    public static PolicyDecision Deny(string reason) => new(false, reason);
}

/// Evaluated before every tool call, in the agent middleware.
///
/// Production: Governance.AcsToolPolicy, the AGT policy engine over per-role files in policies/s2
/// (hand-authored now, mlang-compiled later). RoleAllowListPolicy is the in-code double; both pass
/// ToolPolicyContract. ScopedWorkspace enforces paths itself, so path safety does not depend on
/// either policy.
public interface IToolPolicy
{
    PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args);
}

public static class Roles
{
    public const string Planner = "s2-planner";            // plan-level: handover -> plan.json
    public const string TaskPlanner = "s2-task-planner";    // per-task approach note, read-only
    public const string TestAuthor = "s2-test-author";      // writes tests inside `testWrites`, before any code
    public const string Implementer = "s2-implementer";     // writes code inside `writes`; the tests are fixed by then
}

public sealed class RoleAllowListPolicy : IToolPolicy
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [Roles.Planner] = ["read_file", "list_files"],
        [Roles.TaskPlanner] = ["read_file", "list_files", "raise_needs_decision"],
        [Roles.TestAuthor] = ["read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision"],
        [Roles.Implementer] = ["read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision", "return_to_test_author"],
    };

    public PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args) =>
        Allowed.TryGetValue(role, out var tools) && tools.Contains(toolName)
            ? PolicyDecision.Allow()
            : PolicyDecision.Deny($"role '{role}' may not call '{toolName}'");
}
