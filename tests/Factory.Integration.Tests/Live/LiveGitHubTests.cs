using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Microsoft.FSharp.Collections;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Live;

using Core = Factory.Core.Core;

/// Opt-in: FACTORY_LIVE_GITHUB=owner/repo (a private copy of pricing-sample) and
/// FACTORY_LIVE_APPROVER=<login>. Opens a real PR for T-001 in tiered mode, checks the tier
/// and reasons reached the PR body and the ledger, then closes the PR and deletes its branch.
public class LiveGitHubTests
{
    private static readonly string? Repo = Environment.GetEnvironmentVariable("FACTORY_LIVE_GITHUB");
    private static readonly string Approver = Environment.GetEnvironmentVariable("FACTORY_LIVE_APPROVER") ?? "";

    [Fact]
    public async Task T001_opens_a_PR_whose_body_and_ledger_carry_the_tier_and_reasons_then_parks_for_approve()
    {
        if (string.IsNullOrEmpty(Repo)) return; // opt-in
        var model = new ScriptedChatClient();
        var policy = new MergePolicy(MergeEnforcement.NewTiered(ListModule.OfSeq([Approver])),
            Core.DefaultMergePolicy.AutoMergeMaxRisk, Core.DefaultMergePolicy.AutoMergeMaxChangedLines);
        using var h = await TeamHarness.CloneAsync($"https://github.com/{Repo}.git", model, new Limits(8, 3), policy);
        model.Enqueue(
            Say(JsonSerializer.Serialize(new
            {
                handover = new { id = h.Handover.Id, digest = h.Handover.Digest },
                tasks = new[]
                {
                    new
                    {
                        id = "T-001", title = "Add QuoteStatus", instructions = "Add the QuoteStatus enum.",
                        elements = new[] { "Pricing Platform/Quotes DB" }, writes = new[] { "src/Pricing.Data/**" },
                        testWrites = new[] { "tests/Pricing.Data.Tests/**" }, dependsOn = Array.Empty<string>(), covers = new[] { "D-0001" },
                        verify = new[] { new { name = "file exists", kind = "invariant", run = $"sh {TeamHarness.QuoteStatusTest}", timeoutSeconds = 30 } },
                    },
                },
            })),
            Say("Approach: add QuoteStatus.cs"),
            Call("write_file", new { path = TeamHarness.QuoteStatusTest, content = "test -f src/Pricing.Data/QuoteStatus.cs\n" }),
            Call("declare_done", new { summary = "red test for QuoteStatus" }), Say("done"),
            Call("write_file", new { path = "src/Pricing.Data/QuoteStatus.cs", content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()),
            new NoHuman(), default);

        Assert.Equal("parked", result?.Status);
        var url = result!.Detail.Split(' ')[1];
        try
        {
            var body = await Gh(h.Repo, "pr", "view", url, "--json", "body", "--jq", ".body");
            Assert.Contains("Merge tier: TwoApprovals", body);
            Assert.Contains("high risk", body);
            var merge = Assert.Single(h.Rows(LedgerKinds.Merge));
            Assert.Equal("TwoApprovals", merge.Outcome);
            Assert.Contains("high risk", merge.Payload);
            Assert.Contains(url, merge.Payload);
        }
        finally
        {
            await Gh(h.Repo, "pr", "close", url);
            await Gh(h.Repo, "api", "-X", "DELETE", $"repos/{Repo}/git/refs/heads/{Factory.S2.Git.GitWorkspace.IntegrationBranch(TeamHarness.RunId)}");
        }
    }

    private sealed class NoHuman : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("live tiered mode reads PR comments, never the console");
    }

    private static async Task<string> Gh(string repo, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("gh", args, repo, TimeSpan.FromMinutes(1));
        return r.Succeeded ? r.Output.Trim() : throw new InvalidOperationException(r.Tail());
    }
}
