using System.ComponentModel;
using System.Diagnostics;

namespace Factory.Harness.Registry;

/// Where S2 gets a task's context bundle. Never throws (cancellation aside): every failure is Missed and the run
/// continues without a bundle (trace bundle_id null). S2 must work when the S1 core is down.
public interface IContextSource
{
    Task<BundleFetch> FetchAsync(BundleRequest request, CancellationToken ct);
}

/// No bundle, by configuration.
public sealed class NoContextSource : IContextSource
{
    public Task<BundleFetch> FetchAsync(BundleRequest request, CancellationToken ct) =>
        Task.FromResult<BundleFetch>(new BundleFetch.Missed("none", "no context source configured"));
}

/// A bundle designc wrote into the handover folder: bundles/&lt;role&gt;.json (not a frozen file, so outside the integrity check).
public sealed class FileContextSource(string handoverDir) : IContextSource
{
    public async Task<BundleFetch> FetchAsync(BundleRequest request, CancellationToken ct)
    {
        BundleFetch Miss(string reason) => new BundleFetch.Missed("file", reason);
        // The role names a file: it must stay inside bundles/.
        if (request.Role.Length == 0 || request.Role.IndexOfAny(['/', '\\']) >= 0 || request.Role.Contains(".."))
            return Miss($"role '{request.Role}' is not a file name");
        var path = Path.Combine(handoverDir, "bundles", request.Role + ".json");
        if (!File.Exists(path)) return Miss($"no {Path.Combine("bundles", request.Role + ".json")} in the handover");
        string text;
        try { text = await File.ReadAllTextAsync(path, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Miss($"could not read {path}: {e.Message}"); }
        return ContextBundle.Parse(text, request, "file");
    }
}

/// designc's headless core: `node scripts/designc-core.js bundle --registry R --role r ... --json`.
/// Only stdout is parsed (a warning on stderr must not corrupt the JSON); a non-zero exit, a timeout or a node that
/// will not start is Missed.
public sealed class DesigncCliContextSource(string script, string registry, string node, TimeSpan timeout) : IContextSource
{
    public static IReadOnlyList<string> Arguments(string script, string registry, BundleRequest r)
    {
        var args = new List<string> { script, "bundle", "--registry", registry, "--role", r.Role };
        if (r.Handover is not null) args.AddRange(["--handover", r.Handover]);
        if (r.WorkItem is not null) args.AddRange(["--item", r.WorkItem]);
        if (r.Task is not null) args.AddRange(["--task", r.Task]);
        foreach (var e in r.Elements) args.AddRange(["--element", e]);
        args.Add("--json");
        return args;
    }

    public async Task<BundleFetch> FetchAsync(BundleRequest request, CancellationToken ct)
    {
        BundleFetch Miss(string reason) => new BundleFetch.Missed("designc", reason);
        var psi = new ProcessStartInfo(node) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in Arguments(script, registry, request)) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return Miss($"{node} did not start");
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            var text = await stdout;
            await stderr;
            return p.ExitCode == 0 ? ContextBundle.Parse(text, request, "designc") : Miss($"designc-core exit {p.ExitCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Miss($"timed out after {timeout.TotalSeconds:0.#}s");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return Miss($"could not run {node}: {e.Message}");
        }
        finally
        {
            if (p is not null)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.Dispose();
            }
        }
    }
}
