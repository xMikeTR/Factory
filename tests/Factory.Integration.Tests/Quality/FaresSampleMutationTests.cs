using System.Diagnostics;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Quality;
using Factory.S2.Verify;
using Xunit;
using Xunit.Abstractions;

namespace Factory.Integration.Tests.Quality;

/// F6 slice 6, measure and report: on a copy of fares-sample with one changed line, what the Factory's per-file `--mutate` finds against what
/// `dotnet-stryker --since:<sha>` finds. Numbers go to the test output and to FACTORY_FARES_SAMPLE_OUT when set; nothing is asserted on them
/// and no code follows from them. Opt-in: FACTORY_TEST_DOCKER=1 and FACTORY_FARES_SAMPLE=<path to fares-sample>.
[Trait("needs", "docker")]
public sealed class FaresSampleMutationTests(ITestOutputHelper output)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private static readonly string? Sample = Environment.GetEnvironmentVariable("FACTORY_FARES_SAMPLE");

    private static async Task Git(string dir, params string[] args) =>
        Assert.True((await ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromSeconds(60))).Succeeded);

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            if (!IsBuildOutput(from, dir)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            if (!IsBuildOutput(from, file)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    private static bool IsBuildOutput(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".git");

    private async Task<string> RecordAsync(string label, string worktree, Func<Task<MutationRun>> run)
    {
        var sw = Stopwatch.StartNew();
        var result = await run();
        var by = result.Mutants.GroupBy(m => m.File).Select(g => $"{g.Key}={g.Count()}");
        var line = $"{label}: {result.Mutants.Count} mutants, timed_out={result.TimedOut}, {sw.Elapsed.TotalSeconds:F0}s, files: {(result.Mutants.Count == 0 ? "none" : string.Join(", ", by))}";
        output.WriteLine(line);
        return line;
    }

    [Fact]
    public async Task Per_file_mutate_and_since_on_the_same_one_line_change()
    {
        if (!Enabled || string.IsNullOrEmpty(Sample) || !Directory.Exists(Sample)) return;
        var root = Directory.CreateTempSubdirectory("fares-mutation").FullName;
        Copy(Sample, root);
        await Git(root, "init", "-q", "-b", "main");
        await Git(root, "add", "-A");
        await ProcessRunner.RunAsync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "base"], root, TimeSpan.FromSeconds(60));
        var baseSha = (await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], root, TimeSpan.FromSeconds(30))).Output.Trim();

        // One changed line in each of: the API (not in Fares.sln), the data project (in it, with tests).
        var api = Path.Combine(root, "src", "Fares.Api", "Program.cs");
        File.AppendAllText(api, "\n// F6 probe\n");
        var booking = Path.Combine(root, "src", "Fares.Data", "Booking.cs");
        File.AppendAllText(booking, "\n// F6 probe\n");
        await ProcessRunner.RunAsync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qam", "one line"], root, TimeSpan.FromSeconds(60));

        var sandboxes = new DockerSandboxFactory(new DockerSandboxOptions());
        var lines = new List<string>
        {
            await RecordAsync("per-file --mutate Fares.Api/Program.cs", root, () => new StrykerRunner(sandboxes, TimeSpan.FromMinutes(15)).RunAsync(root, ["src/Fares.Api/Program.cs"], default)),
            await RecordAsync("per-file --mutate Fares.Data/Booking.cs", root, () => new StrykerRunner(sandboxes, TimeSpan.FromMinutes(15)).RunAsync(root, ["src/Fares.Data/Booking.cs"], default)),
        };

        await using (var sandbox = await sandboxes.ForTeamAsync("fares-since", root, default))
        {
            var out1 = $"/tmp/stryker-since-{Guid.NewGuid():N}";
            var sw = Stopwatch.StartNew();
            var run = await sandbox.RunAsync($"dotnet-stryker --solution Fares.sln --since:{baseSha} --reporter json --output {out1} --disable-bail", root, TimeSpan.FromMinutes(15), new Dictionary<string, string>(), default);
            var report = await sandbox.RunAsync($"cat {out1}/reports/mutation-report.json", root, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), default);
            var count = report.Succeeded && report.Output.TrimStart().StartsWith('{') ? StrykerReport.Parse(report.Output, root) : [];
            var by = count.GroupBy(m => m.File).Select(g => $"{g.Key}={g.Count()}");
            var line = $"--since:{baseSha[..7]}: {count.Count} mutants, exit={run.ExitCode}, timed_out={run.TimedOut}, {sw.Elapsed.TotalSeconds:F0}s, files: {(count.Count == 0 ? "none" : string.Join(", ", by))}"
                + (count.Count == 0 ? "; tail: " + run.Tail(400).Replace('\n', ' ') : "");
            output.WriteLine(line);
            lines.Add(line);
        }

        if (Environment.GetEnvironmentVariable("FACTORY_FARES_SAMPLE_OUT") is { Length: > 0 } outFile) File.WriteAllLines(outFile, lines);
        Directory.Delete(root, recursive: true);
    }
}
