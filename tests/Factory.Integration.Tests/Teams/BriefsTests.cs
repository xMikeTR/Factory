using FactoryCore = global::Factory.Core.Core;
using Factory.Core;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.S2.Teams;
using Xunit;

namespace Factory.Integration.Tests.Teams;

public class BriefsTests
{
    /// live2: the planner chose Testcontainers and `pobserve`, neither of which the sandbox has.
    [Theory]
    [InlineData("planner")]
    [InlineData("task-planner")]
    [InlineData("test-author")]
    [InlineData("implementer")]
    public void Every_role_is_told_what_the_sandbox_can_run(string role)
    {
        var prompt = Briefs.Prompt(role);

        Assert.Contains("no network", prompt);
        Assert.Contains("no Docker daemon", prompt);
        Assert.Contains("pobserve-check <specs-dir> <SpecName> <log file|dir>", prompt);
        Assert.Contains("--expect-violation", prompt);
    }

    /// pilot P1: sandbox.md said "Nothing else is installed", so the planner built a C# Jint harness to run a
    /// JavaScript repo's tests. The runner image carries Node and npm (docker/runner/Dockerfile).
    [Theory]
    [InlineData("planner")]
    [InlineData("task-planner")]
    [InlineData("test-author")]
    [InlineData("implementer")]
    public void Every_role_is_told_the_sandbox_runs_Node_so_a_JavaScript_repo_is_tested_with_its_own_runner(string role)
    {
        var prompt = Briefs.Prompt(role);

        Assert.Contains("Node 22", prompt);
        Assert.Contains("node --test", prompt);
        Assert.Contains("not through a .NET harness", prompt);
    }

    /// live3: the planner told the code to log JSON lines, which the PObserve parser cannot read.
    [Fact]
    public void The_planner_is_told_the_line_format_PObserve_parses()
    {
        var prompt = Briefs.Prompt("planner");

        Assert.Contains("`pobserve <event> <payload> key=<id>`", prompt);
        Assert.Contains("pobserve-check db/pmodel/PSpec NoLostUpdate \"$LOGS\"", prompt);
    }

    [Fact]
    public async Task The_logging_contract_names_the_events_and_the_line_format_PObserve_parses()
    {
        var handover = await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"),
            new S2Config([]), CancellationToken.None);

        var brief = Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null).Text;

        Assert.Contains("`eCommitted`", brief);
        Assert.Contains("`pobserve <event> <payload> key=<id>`", brief);
        Assert.Contains("`pobserve eCommitted 3 key=Q-42`", brief);
    }

    [Fact]
    public async Task The_context_section_inlines_each_file_with_why_it_is_there_and_marks_truncation()
    {
        var handover = await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"),
            new S2Config([]), CancellationToken.None);
        PackFile[] context =
        [
            new("src/Quote.cs", ContextSource.TaskWritten, "class Quote {}", false),
            new("src/Repo.cs", ContextSource.NewNominated("Quote is loaded here"), "class Repo {}", false),
            new("src/Big.cs", ContextSource.Default, "class Big {", true),
        ];

        var brief = Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null, context).Text;

        Assert.Contains("## Context", brief);
        Assert.Contains("### `src/Quote.cs` (written by this task)\n```\nclass Quote {}\n```", brief);
        Assert.Contains("### `src/Repo.cs` (Quote is loaded here)", brief);
        Assert.Contains("### `src/Big.cs` (truncated to fit the context budget)", brief);
        Assert.DoesNotContain("## Context", Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null).Text);
    }

    /// What every turn of a role on a task repeats comes first, apart from what this turn adds, so the cache
    /// breakpoint on the brief's first message is read back by the next turn.
    [Fact]
    public async Task The_brief_holds_still_up_to_the_context_pack_and_this_turns_notes_follow()
    {
        var handover = await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"),
            new S2Config([]), CancellationToken.None);
        PackFile[] context = [new("src/Quote.cs", ContextSource.TaskWritten, "class Quote {}", false)];

        var brief = Briefs.Task(handover, Plans.Task("T-004"), [], "note", "Quote.exists failed", "chose B", context);

        Assert.Contains("## How this task is verified", brief.Stable);
        Assert.DoesNotContain("## Context", brief.Stable);
        Assert.StartsWith("\n## Context", brief.Turn);
        foreach (var turn in new[] { "note", "Quote.exists failed", "chose B" }) Assert.Contains(turn, brief.Turn);
        Assert.Equal(brief.Stable + brief.Turn, brief.Text);
        Assert.Equal([brief.Stable, brief.Turn], brief.Messages.Select(m => m.Text));
        Assert.Equal([brief.Stable], Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null).Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task Read_only_references_are_named_with_their_tools_and_a_handover_without_any_has_no_such_section()
    {
        var handover = await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"),
            new S2Config([]), CancellationToken.None);
        var node = System.Text.Json.Nodes.JsonNode.Parse(FactoryCore.HandoverJson(handover))!.AsObject();
        node["references"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["key"] = "wpf", ["kind"] = "legacy" });
        var withRefs = FactoryCore.ParseHandover(node.ToJsonString());

        var brief = Briefs.Task(withRefs, Plans.Task("T-004"), [], null, null, null).Text;

        Assert.Contains("## Read-only references", brief);
        Assert.Contains("`wpf` (legacy)", brief);
        Assert.Contains("`read_reference`", brief);
        Assert.Contains("`list_reference`", brief);
        Assert.Contains("never write", brief);
        Assert.DoesNotContain("## Read-only references", Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null).Text);
    }
}
