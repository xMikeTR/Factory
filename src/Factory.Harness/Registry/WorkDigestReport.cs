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
