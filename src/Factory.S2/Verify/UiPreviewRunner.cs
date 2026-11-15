using System.Text;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Sandbox;
using Factory.S2.Git;

namespace Factory.S2.Verify;

/// `factory-s2 preview`: one request is one render of one screen's preview pictures (docs/ui-contract-verify.md §1).
public sealed record UiPreviewRequest(string Registry, string Screen, string Repo, string DesigncDir, string RunnerImage,
    string? Command = null, TimeSpan? Timeout = null);

/// Exit: 0 rendered, 3 could not run (never a pass), 2 error or refusal. A row only with 0.
public sealed record UiPreviewResult(int ExitCode, string Message, RegistryEvent? Row = null);

/// Renders the screen's spec with the app's real library in the runner image, in a scratch worktree of the repo's HEAD, and
/// copies the result into the registry. The person's checkout is never written. Nothing here is an agent.
public sealed class UiPreviewRunner(ISandboxFactory sandboxes, Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    public const string ChromePath = "/opt/playwright/chromium-bin";

    public async Task<UiPreviewResult> RunAsync(UiPreviewRequest r, CancellationToken ct)
    {
        if (!UiPreview.IsScreenName(r.Screen))
            return new(2, $"'{r.Screen}' is not a screen name (lower-case letters, digits and dashes)");
        var registry = Path.GetFullPath(r.Registry);
        var repo = Path.GetFullPath(r.Repo);
        var specPath = Path.Combine(registry, "ui", "specs", r.Screen + ".json");
        if (!File.Exists(specPath)) return new(2, $"no spec for '{r.Screen}': {specPath} does not exist");

        // The worktree is committed code: a render of uncommitted changes could never be reproduced.
        try
        {
            if ((await new GitWorkspace(repo, Path.Combine(repo, ".factory")).ChangedPathsAsync(repo, ct)).Count > 0)
                return new(2, "the repo has uncommitted changes: a preview is rendered from a commit, so commit first");
        }
        catch (Exception e) when (e is InvalidOperationException) { return new(2, $"{repo} is not a usable git repository: {e.Message}"); }

        var specBytes = await File.ReadAllBytesAsync(specPath, ct);
        var check = await ProcessRunner.RunAsync("python3", UiPreview.CheckArguments(registry), registry, CheckTimeout,
            new Dictionary<string, string> { ["PYTHONPATH"] = Path.Combine(r.DesigncDir, "python") }, ct);
        var refusals = UiPreview.SpecRefusals(check.Output, r.Screen);
        if (refusals.Count > 0)
            return new(2, $"designc check refuses '{r.Screen}':\n" + string.Join("\n", refusals.Select(c => "  - " + c)));

        var scratch = Path.Combine(Path.GetTempPath(), "s2-preview-" + Guid.NewGuid().ToString("N"));
        var add = await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "add", "--detach", scratch, "HEAD"], repo, GitTimeout, ct: ct);
        if (!add.Succeeded) return new(2, "could not create the scratch worktree: " + add.Tail(1000));
        try { return await RenderAsync(r, registry, scratch, specPath, specBytes, ct); }
        finally
        {
            // Even on failure or cancel: the scratch copy never outlives the run.
            await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "remove", "--force", scratch], repo, GitTimeout);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "prune"], repo, GitTimeout);
        }
    }

    private async Task<UiPreviewResult> RenderAsync(UiPreviewRequest r, string registry, string worktree, string specPath, byte[] specBytes, CancellationToken ct)
    {
        var contract = Path.Combine(worktree, "ui-contract");
        var previews = Path.Combine(contract, "previews", r.Screen);
        if (Directory.Exists(previews)) Directory.Delete(previews, recursive: true);

        Directory.CreateDirectory(Path.Combine(contract, "specs"));
        Directory.CreateDirectory(Path.Combine(contract, "manifests"));
        Directory.CreateDirectory(Path.Combine(contract, "tests"));
        File.WriteAllBytes(Path.Combine(contract, "specs", r.Screen + ".json"), specBytes);
        var manifests = Path.Combine(registry, "ui", "pack", "components");
        if (Directory.Exists(manifests))
            foreach (var m in Directory.EnumerateFiles(manifests, "*.json"))
                File.Copy(m, Path.Combine(contract, "manifests", Path.GetFileName(m)), overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(contract, "tests", r.Screen + ".preview.test.tsx"), UiPreview.PreviewTest(r.Screen), ct);

        await using var sandbox = await sandboxes.ForTeamAsync("preview-" + r.Screen, worktree, ct);
        if (sandbox.Kind != "docker")
            return new(2, "previews need the runner image: baselines depend on the platform and its fonts, so a render outside it can never be approved");
        if (sandbox.RestoreRefusal is { Count: > 0 } refusal)
            return new(2, "the restore window was refused:\n" + string.Join("\n", refusal.Select(x => "  - " + x)));
        if (sandbox is DockerSandbox { Restore: { Succeeded: false } failed })
            return new(2, "restore failed:\n" + failed.Tail(2000));

        var command = UiPreview.Command(r.Screen, r.Command);
        var run = await sandbox.RunAsync(command, worktree, r.Timeout ?? DefaultTimeout,
            new Dictionary<string, string> { ["CHROME_PATH"] = ChromePath, ["CI"] = "1" }, ct);
        switch (UiPreview.Interpret(run.ExitCode, run.TimedOut, run.Output))
        {
            case UiPreviewOutcome.Error:
                return new(2, $"the preview run did not produce a readable result (exit {run.ExitCode}{(run.TimedOut ? ", timed out" : "")}):\n" + run.Tail(3000));
            case UiPreviewOutcome.CouldNotRun:
                return new(3, "could_not_run: a test failed, so a state never rendered or a component is not exported by ui-contract/registry.ts:\n" + run.Tail(3000));
        }

        var expected = UiPreview.ExpectedFiles(Encoding.UTF8.GetString(specBytes));
        var missing = expected.Where(f => !File.Exists(Path.Combine(previews, f))).ToList();
        if (missing.Count > 0)
            return new(3, $"could_not_run: {missing.Count} expected file(s) were not written:\n" + string.Join("\n", missing.Take(20).Select(f => "  - " + f)));

        var image = await ProcessRunner.RunAsync(ContainerEngine.Name, ["image", "inspect", "--format", "{{.Id}}", r.RunnerImage], worktree, GitTimeout, ct: ct);
        if (!image.Succeeded || image.Output.Trim().Length == 0)
            return new(2, $"could not read the runner image id of {r.RunnerImage}: " + image.Tail(500));

        // The new render replaces the folder entirely, so a stale placement cannot survive (D3 relies on this to invalidate an approval).
        var target = Path.Combine(registry, "ui", "previews", r.Screen);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        var files = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(previews, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(previews, file).Replace('\\', '/');
            var to = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
            files[rel] = UiPreview.Sha256Hex(await File.ReadAllBytesAsync(to, ct));
        }

        var row = UiPreview.Row(r.Screen, UiPreview.Sha256Hex(specBytes), files, image.Output.Trim(), (clock ?? (() => DateTimeOffset.UtcNow))());
        await RegistryLedgerWriter.AppendAsync(registry, [row], ct);
        return new(0, $"rendered {r.Screen}: {files.Count} files into {target}", row);
    }
}
