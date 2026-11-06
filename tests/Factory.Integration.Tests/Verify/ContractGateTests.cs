using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2.Git;
using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

public sealed class ContractGateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("contract-").FullName;
    private string Api => Directory.CreateDirectory(Path.Combine(_root, "api")).FullName;
    private string Ui => Directory.CreateDirectory(Path.Combine(_root, "ui")).FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VerifyRunner Runner(MemoryLedger ledger) =>
        new(ledger, new GatedSandboxFactory(new LocalSandboxFactory(), new HeavyGate(2)), new GitWorkspace(_root, _root));

    private Task<IReadOnlyList<VerifyOutcome>> Run(MemoryLedger ledger, ContractConvention c) =>
        ContractGate.RunAsync(Runner(ledger), c, "r1", Api, Ui, TimeSpan.FromMinutes(1), CancellationToken.None);

    [Fact]
    public async Task Each_command_runs_in_its_own_repo_and_passes_on_exit_zero()
    {
        var outcomes = await Run(new MemoryLedger(), new ContractConvention("pwd > where.txt", "pwd > where.txt"));

        Assert.Equal([ContractGate.BreakingChange, ContractGate.ClientCompile], outcomes.Select(o => o.Name));
        Assert.All(outcomes, o => Assert.Equal(0, o.ExitCode));
        Assert.Equal(Path.GetFullPath(Api), (await File.ReadAllTextAsync(Path.Combine(Api, "where.txt"))).Trim());
        Assert.Equal(Path.GetFullPath(Ui), (await File.ReadAllTextAsync(Path.Combine(Ui, "where.txt"))).Trim());
    }

    [Fact]
    public async Task A_breaking_change_fails_with_the_command_output_and_does_not_hide_the_client_compile()
    {
        var ledger = new MemoryLedger();

        var outcomes = await Run(ledger, new ContractConvention("echo 'removed GET /quotes' && exit 1", "true"));

        Assert.Equal(1, outcomes[0].ExitCode);
        Assert.Contains("removed GET /quotes", outcomes[0].OutputTail);
        Assert.Equal(0, outcomes[1].ExitCode);
    }

    [Fact]
    public async Task A_client_that_does_not_compile_fails_the_gate()
    {
        var outcomes = await Run(new MemoryLedger(), new ContractConvention("true", "echo 'TS2339' && exit 2"));

        Assert.Equal(0, outcomes[0].ExitCode);
        Assert.Equal(2, outcomes[1].ExitCode);
        Assert.Contains("TS2339", outcomes[1].OutputTail);
    }

    [Fact]
    public async Task A_command_that_is_not_named_does_not_run()
    {
        var outcomes = await Run(new MemoryLedger(), new ContractConvention(null, "true"));

        Assert.Equal([ContractGate.ClientCompile], outcomes.Select(o => o.Name));
    }
}
