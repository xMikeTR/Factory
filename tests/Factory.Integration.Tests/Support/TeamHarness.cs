using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Sandbox;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Verify;

namespace Factory.Integration.Tests.Support;

using Core = Factory.Core.Core;

/// A throwaway git repo (branch main, one commit), an integration worktree, and FactoryServices
/// wired to a scripted model, an in-memory ledger and the local sandbox.
public sealed class TeamHarness : IDisposable
{
    public const string RunId = "run-test";
    /// A test already on main that passes once src/Pricing.Data/QuoteStatus.cs exists.
    public const string QuoteStatusTest = "tests/Pricing.Data.Tests/quote_status.sh";
    /// A suite on main that writes JUnit XML to $FACTORY_TEST_RESULTS: one test, "Quote.exists",
    /// which passes while src/Pricing.Data/Quote.cs exists.
    public const string SuiteCommand = "sh scripts/suite.sh";
    private const string Suite = """
        mkdir -p "$(dirname "$FACTORY_TEST_RESULTS")"
        if test -f src/Pricing.Data/Quote.cs; then r=''; else r='<failure message="Quote.cs is gone"/>'; fi
        printf '<testsuites><testsuite name="s"><testcase classname="Quote" name="exists">%s</testcase></testsuite></testsuites>\n' "$r" > "$FACTORY_TEST_RESULTS"

        """;
    private readonly string _root;

    public string Repo { get; }
    public MemoryLedger Ledger { get; } = new();
    public FactoryServices Services { get; }
    public Handover Handover { get; }

    private TeamHarness(string root, Handover handover, Microsoft.Extensions.AI.IChatClient model, Limits limits, string suiteCommand)
    {
        _root = root;
        Repo = Path.Combine(root, "repo");
        Handover = handover;
        var state = Path.Combine(root, "state");
        var settings = new FactorySettings(Repo, Path.Combine(root, "registry"), "main", state, 3, limits,
            ["dotnet build", "dotnet test", "ls"], suiteCommand, null);
        var sandboxes = new LocalSandboxFactory();
        Services = new FactoryServices(settings, Ledger,
            new FactoryAgents(_ => model, new RoleAllowListPolicy(), Ledger),
            new HeuristicConfigurator(),
            new GitWorkspace(Repo, state),
            new VerifyRunner(Ledger, sandboxes),
            sandboxes,
            new DryRunPullRequests(Path.Combine(state, "runs", RunId)));
    }

    /// `suiteCommand` is the integration test command; "true" writes no results, so there is no
    /// regression baseline and the P2P check is skipped.
    public static async Task<TeamHarness> CreateAsync(Microsoft.Extensions.AI.IChatClient model, Limits limits, string suiteCommand = "true")
    {
        var root = Directory.CreateTempSubdirectory("team-").FullName;
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src", "Pricing.Data"));
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "Pricing.Data", "Quote.cs"), "// quote\n");
        Directory.CreateDirectory(Path.Combine(repo, "tests", "Pricing.Data.Tests"));
        await File.WriteAllTextAsync(Path.Combine(repo, QuoteStatusTest), "test -f src/Pricing.Data/QuoteStatus.cs\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(repo, "scripts", "suite.sh"), Suite);
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.name", "factory-test");
        await Git(repo, "config", "user.email", "factory-test@localhost");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "base");

        var handover = await new DesigncHandoverReader().LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), S2Config.Default, CancellationToken.None);
        var h = new TeamHarness(root, handover, model, limits, suiteCommand);
        await h.Services.Git.EnsureIntegrationAsync(RunId, "main", CancellationToken.None);
        return h;
    }

    public static Team TeamOf(PlannedTask task) =>
        new(TeamId.NewTeamId("team-" + task.Id.Value), Core.ToList([task]), task.Writes, task.Elements);

    public Task<ProcessResult> GitIn(string dir, params string[] args) => Git(dir, args);

    private static async Task<ProcessResult> Git(string dir, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromSeconds(30));
        return r.ExitCode == 0 ? r : throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Tail()}");
    }

    /// The handover frozen as the JSON a run starts from.
    public string WriteHandoverJson()
    {
        var path = Path.Combine(_root, "handover.json");
        File.WriteAllText(path, Core.HandoverJson(Handover));
        return path;
    }

    public string StateDir => Services.Settings.StateDir;

    public IEnumerable<LedgerEvent> Rows(string kind) => Ledger.Events.Where(e => e.Kind == kind);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
