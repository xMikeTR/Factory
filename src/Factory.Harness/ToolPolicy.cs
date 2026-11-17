using Factory.Core;

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
    public const string Reviewer = "s2-reviewer";           // judges one task's green diff, read-only; on the reviewer pool
    public const string Qa = "s2-qa";                       // black-box tests of a run's requirements; on the reviewer pool
    public const string UxJudge = "s2-ux-judge";            // scores freeze-time screenshots against the design intent; a gradient, on the reviewer pool
    public const string Repro = "s2-repro";                 // drives a browser to reproduce a reported escape; the spec itself goes in via intake
    public const string Diagnosis = "s2-diagnosis";         // read-only: explains a TranslationError from the trace before the task re-runs
}

public sealed class RoleAllowListPolicy : IToolPolicy
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [Roles.Planner] = ["read_file", "list_files", "submit_plan"],
        [Roles.TaskPlanner] = ["read_file", "list_files", "raise_needs_decision", "submit_approach"],
        [Roles.Reviewer] = ["read_file", "list_files", "submit_review", "confirm_equivalents"],
        [Roles.TestAuthor] = ["read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision", "read_reference", "list_reference", "declare_equivalent"],
        [Roles.Qa] = ["read_file", "list_files", "write_file", "run_command", "submit_qa_tests"],
        [Roles.Implementer] = ["read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision", "read_reference", "list_reference", "return_to_test_author", "check_quality", "playwright_cli"],
        // The browser CLI is for roles that drive a page; never the planner, task planner, reviewer, test author or QA (nor the merge stage, which has no agent).
        [Roles.UxJudge] = ["submit_ux_scores"],
        [Roles.Repro] = ["read_file", "list_files", "playwright_cli", "submit_reproduction"],
        [Roles.Diagnosis] = ["read_file", "list_files", "playwright_cli", "submit_diagnosis"],
    };

    public PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args)
    {
        if (!(Allowed.TryGetValue(role, out var tools) && tools.Contains(toolName)))
            return PolicyDecision.Deny($"role '{role}' may not call '{toolName}'");
        if (toolName == "run_command" && args.TryGetValue("command", out var c) && c?.ToString()?.Contains("--no-verify", StringComparison.Ordinal) == true)
            return PolicyDecision.Deny("--no-verify is not allowed: hooks are not skipped");
        return UiContractRules.Check(toolName, args) is { } denial ? PolicyDecision.Deny(denial) : PolicyDecision.Allow();
    }
}

/// The UI contract's denials, asked of UiPolicyFacts so that RoleAllowListPolicy, the yaml rules (through AcsToolPolicy's derived args) and
/// ScopedWorkspace share one source of facts. Only the roles that hold these tools reach here, and the tools are the only ones that carry
/// paths or commands: build roles are the ones with write_file and run_command, so no draft-screen exception exists for them.
internal static class UiContractRules
{
    public const string PreviewUpdate = "previews are approved in designc; the Factory never updates them";
    public const string ContractWrite = "the UI contract files (previews, specs, manifests, page tests, Vitest config) are the Factory's seeded baseline; build roles never write them";
    public const string ContractShell = "shell commands may not change the UI contract files or reach the holdout";
    public const string HoldoutRead = "holdout samples are not for any agent: they are never read or listed";

    private static string? Arg(IReadOnlyDictionary<string, object?> args, string key) => args.TryGetValue(key, out var v) ? v?.ToString() : null;

    public static string? Check(string tool, IReadOnlyDictionary<string, object?> args) => tool switch
    {
        "run_command" when Arg(args, "command") is { } c => UiPolicyFacts.updatesSnapshots(c) ? PreviewUpdate : UiPolicyFacts.touchesUiContract(c) ? ContractShell : null,
        "write_file" or "delete_file" when Arg(args, "path") is { } p => UiPolicyFacts.isUiProtected(p) ? ContractWrite : null,
        "read_file" when Arg(args, "path") is { } p => UiPolicyFacts.isHoldout(p) ? HoldoutRead : null,
        "list_files" when Arg(args, "directory") is { } d => UiPolicyFacts.isHoldout(d) ? HoldoutRead : null,
        _ => null,
    };
}
