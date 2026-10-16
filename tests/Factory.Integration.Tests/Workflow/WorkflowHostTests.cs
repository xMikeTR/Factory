using Factory.S2;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// A run whose executor throws ends as an error with that exception as its reason, so result.json and designc's Build view
/// say why, not only the terminal.
public class WorkflowHostTests
{
    private sealed class Throws() : Executor("Waves")
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
            .ConfigureRoutes(routes => routes.AddHandler<StartRun>(HandleAsync))
            .SendsMessage<RunFinished>();

        private ValueTask HandleAsync(StartRun msg, IWorkflowContext context, CancellationToken ct = default) =>
            throw new IOException("disk full");
    }

    [Fact]
    public async Task An_executor_that_throws_ends_the_run_with_its_error_as_a_short_reason()
    {
        var thrower = new Throws();
        var workflow = new WorkflowBuilder(thrower).WithOutputFrom(thrower).Build();

        var result = await WorkflowHost.RunAsync(workflow, new StartRun("run-1", "h.json", null), new ConsoleHumanChannel(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("error", result.Status);
        Assert.StartsWith("Waves failed: ", result.Detail);
        Assert.Equal("Waves failed: IOException: disk full", result.Detail);
    }
}
