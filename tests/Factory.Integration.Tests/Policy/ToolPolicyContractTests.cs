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
        ["read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision", "read_reference", "list_reference", "return_to_test_author"];

    private static readonly Dictionary<string, string[]> MayCall = new()
    {
        [Roles.Planner] = ["read_file", "list_files", "submit_plan"],
        [Roles.TaskPlanner] = ["read_file", "list_files", "raise_needs_decision", "submit_approach"],
        [Roles.Reviewer] = ["read_file", "list_files", "submit_review"],
        [Roles.TestAuthor] = Tools[..^1],
        [Roles.Implementer] = [.. Tools, "check_quality", "playwright_cli"],
        [Roles.UxJudge] = ["submit_ux_scores"],
        [Roles.Repro] = ["read_file", "list_files", "playwright_cli", "submit_reproduction"],
        [Roles.Diagnosis] = ["read_file", "list_files", "playwright_cli", "submit_diagnosis"],
    };

    private static readonly Dictionary<string, object?> Args = new() { ["path"] = "src/x.cs" };

    public static TheoryData<string> AllRoles() =>
        [Roles.Planner, Roles.TaskPlanner, Roles.Reviewer, Roles.TestAuthor, Roles.Implementer, Roles.Repro, Roles.Diagnosis, Roles.UxJudge];

    [Theory]
    [MemberData(nameof(AllRoles))]
    public void A_role_may_call_exactly_its_tools(string role)
    {
        var allowed = Tools.Append("submit_plan").Append("submit_approach").Append("submit_review").Append("check_quality").Append("playwright_cli").Append("submit_reproduction").Append("submit_diagnosis").Append("submit_ux_scores").Where(t => Policy.Evaluate(role, t, Args).Allowed);

        Assert.Equal(MayCall[role], allowed);
    }

    [Theory]
    [InlineData("git commit --no-verify -m x", false)]
    [InlineData("git push --no-verify", false)]
    [InlineData("dotnet test tests/X", true)]
    public void Run_command_with_no_verify_is_denied_whatever_the_role_may_otherwise_run(string command, bool allowed)
    {
        var d = Policy.Evaluate(Roles.Implementer, "run_command", new Dictionary<string, object?> { ["command"] = command });

        Assert.Equal(allowed, d.Allowed);
        if (!allowed) Assert.Contains("no-verify", d.Reason);
    }

    private static readonly string[] BuildRoles = [Roles.TestAuthor, Roles.Implementer, Roles.Qa];
    private static readonly string[] FileReaders = [Roles.Planner, Roles.TaskPlanner, Roles.Reviewer, Roles.TestAuthor, Roles.Implementer, Roles.Qa, Roles.Repro, Roles.Diagnosis];

    /// The in-code policy gives the reason text; the yaml layer names the rule that denied (and its message). Either names the rule.
    private static void NamesTheRule(PolicyDecision d, string text, string rule) =>
        Assert.True(d.Reason.Contains(text) || d.Reason.Contains(rule), $"reason names neither '{rule}' nor its text: {d.Reason}");

    private PolicyDecision Run(string role, string tool, string key, string value) =>
        Policy.Evaluate(role, tool, new Dictionary<string, object?> { [key] = value });

    public static TheoryData<string, string> BuildRolesAndCommands(string[] commands)
    {
        var d = new TheoryData<string, string>();
        foreach (var r in BuildRoles) foreach (var c in commands) d.Add(r, c);
        return d;
    }

    public static TheoryData<string, string> SnapshotUpdates() => BuildRolesAndCommands(
    [
        "npx vitest run -u", "npx vitest run --project ui-page -u", "npx vitest run --project ui-page --update", "npx vitest run --project ui-preview -u claims.preview",
        "npm run ui:preview", "npm run ui:preview -- bordereaux.preview", "echo ok\nnpx vitest run --project ui-page -u", "cd x && npx vitest run -u",
    ]);

    // Preview updates are denied for build roles always: no draft-screen exception.
    [Theory]
    [MemberData(nameof(SnapshotUpdates))]
    public void A_build_role_may_never_update_snapshots(string role, string command)
    {
        var d = Run(role, "run_command", "command", command);

        Assert.False(d.Allowed);
        NamesTheRule(d, "previews are approved in designc; the Factory never updates them", "deny-preview-update");
    }

    public static TheoryData<string, string> ContractShellWrites() => BuildRolesAndCommands(
    [
        "rm ui-contract/previews/bordereaux/ready-chromium-linux.png", "cp /tmp/x.png ui-contract/previews/bordereaux/ready.png",
        "echo x > ui-contract/previews/bordereaux/ready.aria.yml", "sed -i 's/0.001/0.5/' ui-contract/preview/vitest-ui.tsx",
        "git checkout main -- ui-contract/previews", "cd ui-contract && rm previews/bordereaux/ready.aria.yml", "sed -i s/a/b/ vitest.config.ts",
        "Copy-Item x.png ui-contract\\previews\\bordereaux\\ready-chromium-win32.png",
        "cat ui-contract/holdout/bordereaux.json", "cd ui-contract && cat holdout/bordereaux.json", "cat ui/holdout/bordereaux.json",
    ]);

    [Theory]
    [MemberData(nameof(ContractShellWrites))]
    public void A_build_role_may_not_change_the_ui_contract_or_reach_the_holdout_from_the_shell(string role, string command)
    {
        var d = Run(role, "run_command", "command", command);

        Assert.False(d.Allowed);
        NamesTheRule(d, "UI contract", "deny-ui-contract-shell");
    }

    public static TheoryData<string, string> ProtectedPaths()
    {
        var d = new TheoryData<string, string>();
        foreach (var r in BuildRoles)
            foreach (var p in new[]
            {
                "ui-contract/previews/bordereaux/ready-chromium-linux.png", "ui-contract/previews/bordereaux/ready.aria.yml",
                "ui-contract/previews/bordereaux/placements/empty__premium-chromium-linux.png", "ui-contract/specs/bordereaux.json",
                "ui-contract/specs/claims.json", "ui-contract/manifests/DataTable.json", "ui-contract/preview/vitest-ui.tsx",
                "ui-contract/tests/bordereaux.page.test.tsx", "vitest.config.ts", ".claude/hooks/ui-contract-guard.mjs",
                "src/pages/../../ui-contract/previews/bordereaux/ready.aria.yml", "./src/../ui-contract/previews/bordereaux/ready.png",
                "ui-contract\\previews\\bordereaux\\ready.aria.yml", "/work/app/ui-contract/previews/bordereaux/ready.aria.yml",
                "ui-contract/holdout/bordereaux.json", "ui/holdout/bordereaux.json",
            })
                d.Add(r, p);
        return d;
    }

    [Theory]
    [MemberData(nameof(ProtectedPaths))]
    public void A_build_role_may_not_write_or_delete_a_ui_contract_file(string role, string path)
    {
        foreach (var tool in new[] { "write_file", "delete_file" }.Where(t => Run(role, t, "path", "src/x.cs").Allowed))
        {
            var d = Run(role, tool, "path", path);
            Assert.False(d.Allowed);
            NamesTheRule(d, "UI contract", "deny-ui-contract-write");
        }
    }

    public static TheoryData<string, string> HoldoutReads()
    {
        var d = new TheoryData<string, string>();
        foreach (var r in FileReaders) foreach (var p in new[] { "ui-contract/holdout/bordereaux.json", "ui/holdout/bordereaux.json", "src/../ui-contract/holdout/x.json", "ui-contract\\holdout\\x.json" }) d.Add(r, p);
        return d;
    }

    [Theory]
    [MemberData(nameof(HoldoutReads))]
    public void No_role_that_reads_files_may_read_or_list_the_holdout(string role, string path)
    {
        var read = Run(role, "read_file", "path", path);
        var list = Run(role, "list_files", "directory", path);

        Assert.False(read.Allowed);
        NamesTheRule(read, "holdout samples", "deny-holdout-read");
        Assert.False(list.Allowed);
        NamesTheRule(list, "holdout samples", "deny-holdout-read");
    }

    [Theory]
    [InlineData("run_command", "command", "npx vitest run --project ui-page")]
    [InlineData("run_command", "command", "npm run ui:holdout")]
    [InlineData("run_command", "command", "npx vitest related src/pages/Claims.tsx --run")]
    [InlineData("run_command", "command", "ls ui-contract/previews/bordereaux")]
    [InlineData("run_command", "command", "cat ui-contract/specs/bordereaux.json")]
    [InlineData("run_command", "command", "git push -u origin feature/claims")]
    [InlineData("run_command", "command", "sort -u names.txt")]
    [InlineData("read_file", "path", "ui-contract/previews/bordereaux/ready.aria.yml")]
    [InlineData("read_file", "path", "ui-contract/specs/bordereaux.json")]
    [InlineData("list_files", "directory", "ui-contract/previews/bordereaux")]
    [InlineData("write_file", "path", "src/pages/Bordereaux.tsx")]
    [InlineData("write_file", "path", "ui-contract/ui-tests/claims.page.test.tsx")]
    [InlineData("delete_file", "path", "src/pages/Old.tsx")]
    public void The_work_that_must_stay_possible_stays_allowed(string tool, string key, string value)
    {
        foreach (var role in BuildRoles.Where(r => Run(r, tool, key, "src/x.cs").Allowed))
            Assert.True(Run(role, tool, key, value).Allowed, $"{role} {tool} {value}");
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

        Assert.Equal([Roles.Diagnosis, Roles.Implementer, JiraTransitionGate.Role, Roles.Planner, Roles.Qa, Roles.Repro, Roles.Reviewer, Roles.TaskPlanner, Roles.TestAuthor, Roles.UxJudge], policy.Roles.Order());
    }

    private static readonly string[] EveryTool =
    [
        "read_file", "list_files", "write_file", "delete_file", "run_command", "declare_done", "raise_needs_decision", "read_reference", "list_reference",
        "return_to_test_author", "declare_equivalent", "check_quality", "playwright_cli", "submit_plan", "submit_approach", "submit_review", "confirm_equivalents",
        "submit_qa_tests", "submit_reproduction", "submit_diagnosis", "submit_ux_scores",
    ];

    [Fact]
    public void Each_shipped_policy_file_allows_exactly_what_the_in_code_allow_list_does()
    {
        var files = AcsToolPolicy.Load(AcsToolPolicy.DefaultDirectory);
        var code = new RoleAllowListPolicy();
        var args = new Dictionary<string, object?>();

        foreach (var role in files.Roles)
            Assert.Equal(
                EveryTool.Where(t => code.Evaluate(role, t, args).Allowed),
                EveryTool.Where(t => files.Evaluate(role, t, args).Allowed));
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

    [Fact]
    public void A_missing_policy_directory_is_a_rejected_setup_naming_it()
    {
        var missing = Path.Combine(Path.GetTempPath(), "s2-nopolicy-" + Guid.NewGuid().ToString("N"));

        var e = Assert.Throws<FactoryRejectedException>(() => AcsToolPolicy.Load(missing));

        Assert.Contains(e.Errors, x => x.Contains(missing));
    }
}
