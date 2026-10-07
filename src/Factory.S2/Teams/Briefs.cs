using System.Text;
using Factory.Core;

namespace Factory.S2.Teams;

/// System prompts live in prompts/*.md so they are reviewed like code and System 4 proposals can
/// target them by file. Turn briefs are built fresh every turn from the plan, the handover and the
/// ledger: agents get a structured brief, never a growing chat history.
public static class Briefs
{
    private static readonly string PromptDir = Path.Combine(AppContext.BaseDirectory, "prompts");

    public static string Prompt(string name) => File.ReadAllText(Path.Combine(PromptDir, name + ".md"));

    public static string Task(
        Handover handover, PlannedTask task,
        string? approachNote, string? lastFailures, string? resolvedDecision)
    {
        var elements = handover.Elements.Where(e => task.Elements.Contains(e.Id)).ToList();
        var decisions = handover.Decisions
            .Where(d => task.Covers.Contains(d.Id) || d.Binds.Any(b => task.Elements.Contains(b)))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Task {task.Id.Value}: {task.Title}").AppendLine().AppendLine(task.Instructions).AppendLine();

        sb.AppendLine("## Elements");
        foreach (var e in elements) sb.AppendLine($"- {e.Name} (`{e.Id.Value}`), risk {e.Risk}");

        sb.AppendLine().AppendLine("## You may write only these paths");
        foreach (var w in task.Writes.Concat(task.TestWrites)) sb.AppendLine($"- `{w.Value}`");

        if (decisions.Count > 0)
        {
            sb.AppendLine().AppendLine("## Bound decisions you must honour");
            foreach (var d in decisions)
                sb.AppendLine($"- {d.Id.Value}{(task.Covers.Contains(d.Id) ? " (this task proves it)" : "")}: {d.Statement}");
        }

        if (!handover.EventVocabulary.IsEmpty)
        {
            sb.AppendLine().AppendLine("## Logging contract");
            sb.AppendLine("Where the code emits these events, use exactly these names; PObserve checks production logs against them:");
            sb.AppendLine(string.Join(", ", handover.EventVocabulary.Select(v => $"`{v}`")));
        }

        sb.AppendLine().AppendLine("## How this task is verified (fixed; you cannot change these)");
        foreach (var v in task.Verify)
        {
            switch (v)
            {
                case VerifyEntry.Automated { Item: var a }:
                    sb.AppendLine($"- {a.Name} [{(a.Kind.IsSelfSeeded ? "self-seeded" : "invariant")}]: `{a.Run}`");
                    break;
                case VerifyEntry.Manual { Item: var m }:
                    sb.AppendLine($"- {m.Name} [manual, reviewed at merge]: {m.Instructions}");
                    break;
            }
        }

        if (approachNote is not null)
            sb.AppendLine().AppendLine("## Approach note from the planning turn").AppendLine(approachNote);
        if (resolvedDecision is not null)
            sb.AppendLine().AppendLine("## Decision resolved since your last turn").AppendLine(resolvedDecision);
        if (lastFailures is not null)
            sb.AppendLine().AppendLine("## What failed last time").AppendLine(lastFailures);

        return sb.ToString();
    }

    public static string Failures(IEnumerable<VerifyOutcome> outcomes) =>
        string.Join("\n\n", outcomes.Where(o => o.ExitCode != 0)
            .Select(o => $"### {o.Name} (exit {o.ExitCode})\n```\n{o.OutputTail}\n```"));
}
