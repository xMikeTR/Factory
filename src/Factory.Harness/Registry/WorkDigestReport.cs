using System.Diagnostics;
using System.Text;
using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.Harness.Registry;

/// The work digest for a registry: what a person looks at, in four buckets. The commit-on-main
/// check is the only I/O beyond reading the registry, and it is optional (no repo, nothing judged).
public static class WorkDigestReport
{
    public static async Task<(Digest Digest, IReadOnlyList<string> Problems)> BuildAsync(
        string registryPath, string? repoPath, string baseBranch, long lastLook, int defaultWip, CancellationToken ct)
    {
        var registry = await WorkRegistryReader.ReadAsync(registryPath, ct);
        var commits = registry.Facts.Select(f => f.Fact).OfType<LedgerFact.ItemDone>()
            .Select(d => d.commit).Where(c => FSharpOption<string>.get_IsSome(c)).Select(c => c.Value).Distinct().ToList();
        var onMain = new Dictionary<string, bool?>();
        foreach (var c in commits) onMain[c] = repoPath is null ? null : await IsOnAsync(repoPath, baseBranch, c, ct);

        var digest = DigestModule.compute(
            ListModule.OfSeq(registry.Items),
            ListModule.OfSeq(registry.Facts.Select(f => Tuple.Create(f.Ts, f.Fact))),
            FuncConvert.FromFunc<string, FSharpOption<bool>>(c =>
                onMain.GetValueOrDefault(c) is { } b ? FSharpOption<bool>.Some(b) : FSharpOption<bool>.None),
            defaultWip, lastLook);
        return (digest, registry.Problems);
    }

    /// S4's work metrics over the whole ledger (every Done, not only since a last look).
    public static async Task<WorkMetrics> MetricsAsync(
        string registryPath, string? repoPath, string baseBranch, CancellationToken ct)
    {
        var registry = await WorkRegistryReader.ReadAsync(registryPath, ct);
        var (digest, _) = await BuildAsync(registryPath, repoPath, baseBranch, 0, 3, ct);
        return Metrics.compute(
            ListModule.OfSeq(registry.Items),
            ListModule.OfSeq(registry.Facts.Select(f => Tuple.Create(f.Ts, f.Fact))),
            digest);
    }

    public static string RenderMetrics(WorkMetrics m)
    {
        static string Days(long ms) => $"{ms / 86_400_000.0:0.#}d";
        static string T(FSharpOption<WorkTiming> t) =>
            FSharpOption<WorkTiming>.get_IsSome(t) ? $"median {Days(t.Value.Median)}, p90 {Days(t.Value.P90)} (n={t.Value.Samples})" : "no data";
        var sb = new StringBuilder();
        sb.AppendLine("Throughput (done per week, by target kind)");
        foreach (var ((week, kind), n) in m.Throughput.Select(x => (x.Item1, x.Item2))) sb.AppendLine($"  {week} {kind}: {n}");
        sb.AppendLine($"Lead time (pull to done): {T(m.LeadTime)}");
        sb.AppendLine($"Human wait: {T(m.HumanWait)}; open now: {m.OpenWaits}");
        sb.AppendLine($"Conflict deferrals: {m.ConflictDeferrals}");
        sb.AppendLine("Re-check rate: " + (FSharpOption<double>.get_IsSome(m.RecheckRate) ? $"{m.RecheckRate.Value:P0}" : "no re-checks"));
        sb.AppendLine("Escapes by origin: " + (m.EscapesByOrigin.IsEmpty ? "none" : string.Join(", ", m.EscapesByOrigin.Select(e => $"{e.Item1} {e.Item2}"))));
        return sb.ToString();
    }

    public static string Render(Digest d, IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder();
        void Bucket(string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            sb.AppendLine($"{title} ({list.Count})");
            foreach (var l in list) sb.AppendLine("  - " + l);
        }
        Bucket("Needs decision", d.Needs.Select(n => n.Text));
        Bucket("Failed a gate", d.Failed.Select(f => f.Text));
        Bucket("Done since last look", d.Done.Select(x => x.Item2 is { } c && FSharpOption<string>.get_IsSome(c) ? $"{x.Item1} at {c.Value[..Math.Min(7, c.Value.Length)]}" : x.Item1));
        Bucket("Waiting", d.Waiting.Select(w => w.Text));
        sb.AppendLine($"WIP {d.Active}/{d.WipLimit}");
        if (problems.Count > 0) Bucket("Work files with problems", problems);
        return sb.ToString();
    }

    private static async Task<bool?> IsOnAsync(string repo, string baseBranch, string commit, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "merge-base", "--is-ancestor", commit, baseBranch }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            return p.ExitCode switch { 0 => true, 1 => false, _ => null };
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
