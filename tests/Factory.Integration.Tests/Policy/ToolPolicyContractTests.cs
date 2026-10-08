using Factory.Core;
using Factory.Harness;
using Factory.Harness.Governance;
using Xunit;

namespace Factory.Integration.Tests.Policy;

/// The same cases against every IToolPolicy: the allow-list double and the ACS (AGT) policy files.
public abstract class ToolPolicyContract
{
    protected abstract IToolPolicy Policy { get; }

    private static readonly string[] Tools =
        ["read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision", "return_to_test_author"];

    private static readonly Dictionary<string, string[]> MayCall = new()
    {
        [Roles.Planner] = ["read_file", "list_files", "submit_plan"],
        [Roles.TaskPlanner] = ["read_file", "list_files", "raise_needs_decision", "submit_approach"],
        [Roles.Reviewer] = ["read_file", "list_files", "submit_review"],
        [Roles.TestAuthor] = Tools[..^1],
        [Roles.Implementer] = Tools,
    };

    private static readonly Dictionary<string, object?> Args = new() { ["path"] = "src/x.cs" };

    public static TheoryData<string> AllRoles() => [Roles.Planner, Roles.TaskPlanner, Roles.Reviewer, Roles.TestAuthor, Roles.Implementer];

    [Theory]
    [MemberData(nameof(AllRoles))]
    public void A_role_may_call_exactly_its_tools(string role)
    {
        var allowed = Tools.Append("submit_plan").Append("submit_approach").Append("submit_review").Where(t => Policy.Evaluate(role, t, Args).Allowed);

        Assert.Equal(MayCall[role], allowed);
    }

    [Fact]
    public void An_unknown_role_or_tool_is_denied()
    {
        Assert.False(Policy.Evaluate("s2-merge", "read_file", Args).Allowed);
        Assert.False(Policy.Evaluate(Roles.Implementer, "http_get", Args).Allowed);
    }

    [Fact]
    public void A_denial_names_the_role_and_the_tool()
    {
        var d = Policy.Evaluate(Roles.Planner, "write_file", Args);

        Assert.Contains($"role '{Roles.Planner}' may not call 'write_file'", d.Reason);
    }
}

public class RoleAllowListPolicyContract : ToolPolicyContract
{
    protected override IToolPolicy Policy { get; } = new RoleAllowListPolicy();
}

public class AcsToolPolicyContract : ToolPolicyContract
{
    protected override IToolPolicy Policy { get; } = AcsToolPolicy.Load(AcsToolPolicy.DefaultDirectory);
}

public class AcsToolPolicyTests
{
    private static string Dir(params (string File, string Yaml)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("acs-").FullName;
        foreach (var (file, yaml) in files) File.WriteAllText(Path.Combine(dir, file), yaml);
        return dir;
    }

    private const string Planner = """
        # source: hand-authored
        apiVersion: governance.toolkit/v1
        version: "1"
        name: s2-planner
        scope: agent
        default_action: deny
        rules:
          - name: read
            condition: "tool_name == 'read_file'"
            action: allow
            priority: 10
        """;

    [Fact]
    public void An_allow_names_the_policy_file_rule_and_source()
    {
        var d = AcsToolPolicy.Load(Dir(("planner.yaml", Planner))).Evaluate(Roles.Planner, "read_file", new Dictionary<string, object?>());

        Assert.True(d.Allowed);
        Assert.Contains("planner.yaml", d.Reason);
        Assert.Contains("'read'", d.Reason);
        Assert.Contains("hand-authored", d.Reason);
    }

    [Fact]
    public void Shipped_policies_load_and_cover_every_role()
    {
        var policy = AcsToolPolicy.Load(AcsToolPolicy.DefaultDirectory);

        Assert.Equal([Roles.Implementer, Roles.Planner, Roles.Reviewer, Roles.TaskPlanner, Roles.TestAuthor], policy.Roles.Order());
    }

    [Theory]
    [InlineData("apiVersion: governance.toolkit/v1", "no '# source:' header")]
    [InlineData("# source: hand-authored\nname: s2-planner\ndefault_action: allow", "default_action must be deny")]
    [InlineData("# source: hand-authored\nname: s2-implementer\ndefault_action: deny", "name 's2-implementer' does not match")]
    public void A_policy_file_that_could_fail_open_or_hides_its_source_is_rejected(string yaml, string error)
    {
        var e = Assert.Throws<FactoryRejectedException>(() => AcsToolPolicy.Load(Dir(("planner.yaml", yaml))));

        Assert.Contains(e.Errors, x => x.Contains(error));
    }
}
