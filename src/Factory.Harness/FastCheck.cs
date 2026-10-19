namespace Factory.Harness;

/// The checks an agent can ask for mid-turn. Parts run side by side under one shared budget (about 3 seconds): a part
/// that does not finish is named as not run and left to the verify gate, so a slow check never holds the turn (and the
/// prompt cache) while the agent waits. Findings are one line each, as verify words them.
public static class FastCheck
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(3);

    public sealed record Part(string Name, Func<CancellationToken, Task<IReadOnlyList<string>>> Run);

    public static async Task<string> RunAsync(IEnumerable<Part> parts, TimeSpan budget, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        var runs = parts.Select(async part =>
        {
            try { return (Lines: (IReadOnlyList<string>)await part.Run(deadline.Token), Note: (string?)null); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ([], Over(part.Name, budget)); }
            catch (Exception e) when (e is not OperationCanceledException) { return ([], $"not run: {part.Name} failed ({e.Message})"); }
        }).ToList();
        var done = await Task.WhenAll(runs);
        ct.ThrowIfCancellationRequested();
        var lines = done.SelectMany(d => d.Lines).ToList();
        var notRun = done.Where(d => d.Note is not null).Select(d => d.Note!).ToList();
        if (lines.Count == 0 && notRun.Count == 0) return "no findings";
        return string.Join("\n", lines.Concat(notRun));
    }

    private static string Over(string name, TimeSpan budget) => $"not run: {name} took more than {budget.TotalSeconds:0.#} s; verify runs it after declare_done";
}
