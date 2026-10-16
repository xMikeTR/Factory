using System.Text.Json;
using Factory.Core;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// Every run leaves `.factory/runs/<id>/result.json` saying how it ended and why, whatever the exit path,
/// so a failure is never only in the terminal's scrollback.
public class RunOutcomeTests : IDisposable
{
    private readonly string _runDir = Path.Combine(Directory.CreateTempSubdirectory("s2-outcome-").FullName, "runs", "r1");
    public void Dispose() => Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_runDir))!, recursive: true);

    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_runDir, "result.json"))).RootElement;

    [Fact]
    public async Task A_run_that_ends_records_its_status_reason_and_exit_code()
    {
        var code = await RunOutcomeFile.RecordAsync(_runDir, "r1", () => Task.FromResult(new RunOutcome("parked", "ND-1 open", 3)));

        Assert.Equal(3, code);
        var r = Result();
        Assert.Equal("r1", r.GetProperty("run").GetString());
        Assert.Equal("parked", r.GetProperty("status").GetString());
        Assert.Equal("ND-1 open", r.GetProperty("reason").GetString());
        Assert.Equal(3, r.GetProperty("exitCode").GetInt32());
        Assert.True(r.GetProperty("at").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_crash_is_recorded_as_failed_with_its_message_and_still_thrown()
    {
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => RunOutcomeFile.RecordAsync(_runDir, "r1",
            () => throw new HttpRequestException("400 Bad Request: credit balance is too low")));

        Assert.Contains("credit balance", e.Message);
        Assert.Equal("failed", Result().GetProperty("status").GetString());
        Assert.Contains("HttpRequestException: 400 Bad Request: credit balance is too low", Result().GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, Result().GetProperty("exitCode").ValueKind);
    }

    [Fact]
    public async Task A_rejected_input_keeps_every_error_in_the_reason()
    {
        await Assert.ThrowsAsync<FactoryRejectedException>(() => RunOutcomeFile.RecordAsync(_runDir, "r1",
            () => throw new FactoryRejectedException("no gateway key", ["set ANTHROPIC_API_KEY"])));

        Assert.Equal("rejected", Result().GetProperty("status").GetString());
        Assert.Contains("set ANTHROPIC_API_KEY", Result().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_cancelled_run_is_recorded_as_cancelled()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunOutcomeFile.RecordAsync(_runDir, "r1",
            () => throw new OperationCanceledException()));

        Assert.Equal("cancelled", Result().GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_resumed_run_replaces_the_earlier_result()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunOutcomeFile.RecordAsync(_runDir, "r1", () => throw new InvalidOperationException("boom")));
        await RunOutcomeFile.RecordAsync(_runDir, "r1", () => Task.FromResult(new RunOutcome("merged", "", 0)));

        Assert.Equal("merged", Result().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Every_exit_path_reports_how_the_attempt_ended_and_a_failing_report_masks_nothing()
    {
        var seen = new List<RunOutcome>();
        Task Ended(RunOutcome o) { seen.Add(o); return Task.CompletedTask; }

        await RunOutcomeFile.RecordAsync(_runDir, "r1", () => Task.FromResult(new RunOutcome("error", "workflow error", 1)), Ended);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunOutcomeFile.RecordAsync(_runDir, "r1", () => throw new OperationCanceledException(), Ended));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunOutcomeFile.RecordAsync(_runDir, "r1", () => throw new InvalidOperationException("boom"), Ended));

        Assert.Equal(["error", "cancelled", "failed"], seen.Select(o => o.Status));
        Assert.Equal("InvalidOperationException: boom", seen[2].Reason);

        var code = await RunOutcomeFile.RecordAsync(_runDir, "r1", () => Task.FromResult(new RunOutcome("merged", "", 0)),
            _ => throw new IOException("registry is read-only"));
        Assert.Equal(0, code);
        Assert.Equal("merged", Result().GetProperty("status").GetString());
    }
}
