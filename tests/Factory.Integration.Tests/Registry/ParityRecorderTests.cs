using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class ParityRecorderTests
{
    [Fact]
    public void The_recorder_compares_every_scenario_of_the_next_app_at_the_commit_and_appends_as_s2()
    {
        var args = ParityRecorder.Arguments("/d/scripts/parity-record.js", new("/reg", "abc1234", "W-1", "http://localhost:5173", null));

        Assert.Equal(["/d/scripts/parity-record.js", "--registry", "/reg", "--scenario", "all", "--side", "next",
            "--commit", "abc1234", "--item", "W-1", "--ledger", "--by", RegistryLedgerProjector.By, "--base-url", "http://localhost:5173"], args);
    }

    [Fact]
    public void Writes_and_extra_requests_are_never_allowed_and_legacy_is_never_the_side()
    {
        var args = ParityRecorder.Arguments("s.js", new("/reg", "abc1234", "W-1", null, "fixture:apps/next.json"));

        Assert.DoesNotContain("--allow-writes", args);
        Assert.DoesNotContain("--allow-request", args);
        Assert.DoesNotContain("legacy", args);
        Assert.DoesNotContain("--base-url", args);
        Assert.Equal("fixture:apps/next.json", args[args.ToList().IndexOf("--adapter") + 1]);
    }

    [Theory]
    [InlineData(0, ParityRecording.Pass)]
    [InlineData(1, ParityRecording.Fail)]
    [InlineData(3, ParityRecording.CouldNotRun)]
    [InlineData(2, ParityRecording.Error)]
    [InlineData(127, ParityRecording.Error)]
    public void Exit_codes_follow_designc(int exit, ParityRecording expected) =>
        Assert.Equal(expected, ParityRecorder.Interpret(exit));
}
