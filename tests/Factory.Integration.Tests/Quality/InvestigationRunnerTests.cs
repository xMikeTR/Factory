using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Quality;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Quality;

public class InvestigationRunnerTests
{
    [Fact]
    public async Task A_diagnosis_is_appended_to_the_blamed_tasks_brief_and_ledgered()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(Call("submit_diagnosis", new { finding = "QuoteStatus never sets Draft on create." }), Say("done"));
        var repair = new RepairFile("run-prev", new Dictionary<string, string> { ["T-001"] = "Frozen spec failed: expected Draft" });

        var diagnosed = await InvestigationRunner.DiagnoseAsync(h.Services, repair, h.Repo, default);

        Assert.Contains("expected Draft", diagnosed.Feedback["T-001"]);
        Assert.Contains("QuoteStatus never sets Draft", diagnosed.Feedback["T-001"]);
        var row = Assert.Single(h.Rows(LedgerKinds.Diagnosis));
        Assert.Equal(("T-001", "diagnosed"), (row.TaskId, row.Outcome));
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public async Task A_task_the_agent_cannot_diagnose_keeps_its_brief()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(Say("I could not tell."));
        var repair = new RepairFile("run-prev", new Dictionary<string, string> { ["T-001"] = "brief" });

        var diagnosed = await InvestigationRunner.DiagnoseAsync(h.Services, repair, h.Repo, default);

        Assert.Equal("brief", diagnosed.Feedback["T-001"]);
        Assert.Empty(h.Rows(LedgerKinds.Diagnosis));
    }

    [Fact]
    public async Task A_reproduction_ledgers_its_steps_or_that_it_was_not_reproduced()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(Call("submit_reproduction", new { steps = "1. open quote 2. save; status stays blank" }), Say("done"), Say("no luck"));

        var steps = await InvestigationRunner.ReproduceAsync(h.Services, "repro-1", "status blank after save", h.Repo, default);
        Assert.Contains("status stays blank", steps);
        Assert.Null(await InvestigationRunner.ReproduceAsync(h.Services, "repro-2", "other", h.Repo, default));

        Assert.Equal(["reproduced", "not_reproduced"], h.Rows(LedgerKinds.Reproduction).Select(r => r.Outcome));
    }
}
