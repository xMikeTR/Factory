using Factory.Harness;
using Factory.Harness.Sandbox;

namespace Factory.Integration.Tests.Support;

/// Stands in for the test project's `dotnet test` of the generated API tests, without dotnet or Docker: answers that one command with a
/// JUnit file in the shape the real logger writes, and hands every other command to the real (local) sandbox.
public sealed class FakeApiSandbox(ISandboxFactory inner) : ISandboxFactory
{
    public enum Mode { Right, WrongRows, NoResults }
    public const string WrongMessage = "ORD-1-ts-01 example 1 (prefix match): orders/Order id=2: tag expected null, got \"Priority\"";

    public Mode Answer { get; set; } = Mode.Right;
    /// The commands the generated tests were run with.
    public List<string> Commands { get; } = [];

    public async Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => new Sandbox(this, await inner.ForTeamAsync(teamId, worktree, ct));

    private static string Case(string title, string? failure) =>
        $"<testcase classname=\"Contracts.ApiScenarios.Ord1Ts01\" name=\"{System.Security.SecurityElement.Escape(title)}\">"
        + (failure is null ? "" : $"<failure message=\"{System.Security.SecurityElement.Escape(failure)}\" type=\"ScenarioMismatch\">stack</failure>") + "</testcase>";

    private sealed class Sandbox(FakeApiSandbox owner, ICommandSandbox real) : ICommandSandbox
    {
        public string Kind => real.Kind;
        public async Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            if (!command.Contains("FullyQualifiedName~Contracts.ApiScenarios")) return await real.RunAsync(command, workdir, timeout, env, ct);
            owner.Commands.Add(command);
            if (owner.Answer == Mode.NoResults) return new(1, "error: could not build", TimeSpan.Zero, false);
            var wrong = owner.Answer == Mode.WrongRows;
            var xml = "<testsuites><testsuite>" + Case("ORD-1-ts-01 example 1: prefix match", wrong ? WrongMessage : null) + Case("ORD-1-ts-01 example 2: whole reference", null) + "</testsuite></testsuites>";
            var path = Path.Combine(workdir, env["FACTORY_TEST_RESULTS"]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, xml, ct);
            return new(wrong ? 1 : 0, wrong ? "Failed!  - Failed: 1" : "Passed!", TimeSpan.Zero, false);
        }
        public ValueTask DisposeAsync() => real.DisposeAsync();
    }
}
