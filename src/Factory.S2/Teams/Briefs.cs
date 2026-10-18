using Microsoft.Extensions.AI;
using System.Text;
using Factory.Core;

namespace Factory.S2.Teams;

/// System prompts live in prompts/*.md so they are reviewed like code and System 4 proposals can
/// target them by file. Turn briefs are built fresh every turn from the plan, the handover and the
/// ledger: agents get a structured brief, never a growing chat history.
public static class Briefs
{
    private static readonly string PromptDir = Path.Combine(AppContext.BaseDirectory, "prompts");

    /// A role's prompt, followed by what the sandbox can run (sandbox.md), which every role needs.
    public static string Prompt(string name) =>
        File.ReadAllText(Path.Combine(PromptDir, name + ".md")).TrimEnd() + "\n" + File.ReadAllText(Path.Combine(PromptDir, "sandbox.md"));

    public static TaskBrief Task(
        Handover handover, PlannedTask task, IEnumerable<PathPattern> writable,
        string? approachNote, string? lastFailures, string? resolvedDecision,
        IReadOnlyList<PackFile>? context = null)
    {
        var elements = handover.Elements.Where(e => task.Elements.Contains(e.Id)).ToList();
        var decisions = handover.Decisions
            .Where(d => task.Covers.Contains(d.Id) || d.Binds.Any(b => task.Elements.Contains(b)))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Task {task.Id.Value}: {task.Title}").AppendLine().AppendLine(task.Instructions).AppendLine();

        sb.AppendLine("## Elements");
        foreach (var e in elements) sb.AppendLine($"- {e.Name} (`{e.Id.Value}`), risk {e.Risk}");

        // The criteria about this task's elements; the frozen specs that judge them are not yours to change.
        var criteria = handover.AcceptanceCriteria
            .Where(c => elements.Any(e => c.Element == e.Name || c.Element.EndsWith("/" + e.Name, StringComparison.Ordinal)))
            .ToList();
        if (criteria.Count > 0)
        {
            sb.AppendLine().AppendLine("## Acceptance criteria for these elements (frozen; the specs generated from them judge your work and are not yours to edit)");
            foreach (var c in criteria) sb.AppendLine($"### {c.Id}").AppendLine(c.Text.TrimEnd());
        }

        sb.AppendLine().AppendLine("## You may write only these paths");
        foreach (var w in writable) sb.AppendLine($"- `{w.Value}`");

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
            sb.AppendLine($"Log each event as one line containing `pobserve <event> <payload> key=<id>` (a scalar payload, or none), e.g. `pobserve {handover.EventVocabulary.Head} 3 key=Q-42`.");
            sb.AppendLine("The key names the entity whose history the spec follows (e.g. the quote id): each key is checked on its own, so logs from many entities can share a file. Without a key, every event is one history.");
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

        var stable = sb.ToString();
        sb.Clear();

        if (context is { Count: > 0 })
        {
            sb.AppendLine().AppendLine("## Context")
              .AppendLine("Current contents of the files you are most likely to need. You do not have to read them again.");
            foreach (var f in context)
            {
                var why = f.Truncated ? "truncated to fit the context budget"
                    : f.Source is ContextSource.Nominated n ? n.reason
                    : f.Source.IsTaskWritten ? "written by this task"
                    : null;
                sb.AppendLine().AppendLine($"### `{f.Path}`{(why is null ? "" : $" ({why})")}").AppendLine("```").AppendLine(f.Text).AppendLine("```");
            }
        }

        if (approachNote is not null)
            sb.AppendLine().AppendLine("## Approach note from the planning turn").AppendLine(approachNote);
        if (resolvedDecision is not null)
            sb.AppendLine().AppendLine("## Decision resolved since your last turn").AppendLine(resolvedDecision);
        if (lastFailures is not null)
            sb.AppendLine().AppendLine("## What failed last time").AppendLine(lastFailures);

        return new TaskBrief(stable, sb.ToString());
    }

    public static string Failures(IEnumerable<VerifyOutcome> outcomes) =>
        string.Join("\n\n", outcomes.Where(o => o.ExitCode != 0)
            .Select(o => $"### {o.Name} (exit {o.ExitCode})\n```\n{o.OutputTail}\n```"));
}

/// A task brief in two parts: what every turn of a role on the task repeats, and what this turn adds (the context
/// pack, which changes as the task writes, and the notes and failures since the last turn).
public sealed record TaskBrief(string Stable, string Turn)
{
    public string Text => Stable + Turn;

    /// One user message per part: PromptCache puts the brief's breakpoint on the first, so the role's next turn on
    /// the task reads tools, instructions and the stable part from cache.
    public IEnumerable<ChatMessage> Messages =>
        Turn.Length == 0 ? [new(ChatRole.User, Stable)] : [new(ChatRole.User, Stable), new(ChatRole.User, Turn)];
}
