using Factory.Core;
using Factory.Harness.Policy;
using Microsoft.FSharp.Collections;

namespace Factory.Harness.Routing;

using Core = Factory.Core.Core;

/// One ledger `route` row: what a (task, role) pair was routed to. Not in RegistryStreams: designc's contract does
/// not know the kind, so it goes in the run ledger only.
public sealed record RouteRow(string TaskId, string Role, string Size, string Risk,
    IReadOnlyDictionary<string, string> Ladder, IReadOnlyList<string> Warnings);

/// The ladders chosen at run start, found again by (task, role) when an agent for that pair is created.
public sealed class RouteTable
{
    public static readonly RouteTable Empty = new([], []);

    private readonly Dictionary<(string Task, string Role), FSharpMap<ModelTier, ModelEntry>> _ladders;

    internal RouteTable(Dictionary<(string, string), FSharpMap<ModelTier, ModelEntry>> ladders, IReadOnlyList<RouteRow> rows)
    {
        _ladders = ladders;
        Rows = rows;
    }

    public IReadOnlyList<RouteRow> Rows { get; }

    /// The ladder for a task's role (a `Roles` constant); null when the pair was not routed.
    public FSharpMap<ModelTier, ModelEntry>? Find(string taskId, string role) =>
        _ladders.TryGetValue((taskId, role), out var l) ? l : null;
}

/// Routes every (task, role) pair once, before any wave starts (FACTORY_ROUTING=catalog). The seed is a stable hash of
/// runId|taskId|role, so a resume reproduces the same ladders. Budget is always Ok and stats are empty until later phases.
///
/// Routed per task: task-planner, test-author, implementer, reviewer. Not routed here: the run-level planner (no task
/// yet) and QA, UX judge, repro and diagnosis (no task id); they stay on the env-var models.
public static class RunRouter
{
    private static readonly (string Name, Role Role)[] Routed =
    [
        // the implementer first: the reviewer must differ from the families its ladder uses
        (Roles.Implementer, Role.Implementer), (Roles.TestAuthor, Role.TestAuthor),
        (Roles.TaskPlanner, Role.TaskPlanner), (Roles.Reviewer, Role.Reviewer),
    ];

    /// Throws FactoryRejectedException, listing every model's reasons, when a pair has no eligible model, needs a
    /// decision, or is over budget.
    /// `contextChars` is the size of the task's brief; at run start only the task's own text is known, so the default
    /// is title plus instructions (a lower bound: the context bundle is added at turn time).
    public static RouteTable Route(string runId, Handover handover, IEnumerable<PlannedTask> tasks, IReadOnlyList<ModelEntry> catalog,
        RoutingPolicyFile.RoutingConfig config, Func<PlannedTask, int>? contextChars = null)
    {
        contextChars ??= t => t.Title.Length + t.Instructions.Length;
        var ladders = new Dictionary<(string, string), FSharpMap<ModelTier, ModelEntry>>();
        var rows = new List<RouteRow>();
        var refusals = new List<string>();

        foreach (var task in tasks)
        {
            var implementerFamilies = new HashSet<string>();
            foreach (var (name, role) in Routed)
            {
                var spec = Core.TaskSpecFor(config.Spec, handover, task, role, contextChars(task), implementerFamilies);
                var outcome = Core.RouteTask(config.Policy, Core.StableSeed($"{runId}|{task.Id.Value}|{name}"), spec, catalog);
                switch (outcome)
                {
                    case RouteOutcome.Routed routed:
                        var ladder = routed.ladder;
                        ladders[(task.Id.Value, name)] = ladder;
                        if (role == Role.Implementer)
                            foreach (var kv in ladder) implementerFamilies.Add(kv.Value.Family);
                        rows.Add(new RouteRow(task.Id.Value, name, spec.Size.ToString(), spec.Risk.ToString(),
                            ladder.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Id), [.. routed.warnings]));
                        break;
                    case RouteOutcome.NoEligibleModel none:
                        refusals.Add($"{task.Id.Value} / {name}: no eligible model");
                        refusals.AddRange(none.Item.Select(i => $"  {i.Model}: {string.Join("; ", i.Reasons)}"));
                        break;
                    case RouteOutcome.NeedsDecision d:
                        refusals.Add($"{task.Id.Value} / {name}: needs a decision: {d.reason}");
                        break;
                    default:
                        refusals.Add($"{task.Id.Value} / {name}: budget exceeded");
                        break;
                }
            }
        }
        return refusals.Count > 0
            ? throw new FactoryRejectedException("routing found no model for the run", [.. refusals])
            : new RouteTable(ladders, rows);
    }
}
