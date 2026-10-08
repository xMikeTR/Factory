using Factory.Core;
using Factory.Harness.Sandbox;

namespace Factory.S2.Quality;

/// Runs ESLint in the sandbox with `complexity` at 0 so every function is reported, using the repo's own config and
/// parser. Earlier source (the baseline) goes over stdin under the same file name, so the same config applies to it.
/// No report (eslint missing or crashed) measures nothing: unmeasured code is not judged.
public static class EslintRunner
{
    private static readonly System.Text.RegularExpressions.Regex SafePath = new(@"^[A-Za-z0-9_./-]+$");
    private const string Rule = "--rule 'complexity: [warn, 0]' --format json";

    public static async Task<IReadOnlyList<FunctionMetrics>> MeasureAsync(
        ICommandSandbox sandbox, string worktree, string file, string? source, CancellationToken ct)
    {
        if (!SafePath.IsMatch(file) || file.Contains("..")) return [];
        var command = source is null
            ? $"npx --no-install eslint {Rule} -- '{file}'"
            : StdinCommand(file, source);
        var r = await sandbox.RunAsync(command, worktree, TimeSpan.FromMinutes(2), new Dictionary<string, string>(), ct);
        var start = r.Output.IndexOf("[{", StringComparison.Ordinal) is var i and >= 0 ? i : r.Output.IndexOf("[]", StringComparison.Ordinal);
        if (start < 0) return [];
        try { return EslintMetrics.Parse(r.Output[start..], worktree); }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static string StdinCommand(string file, string source)
    {
        var eof = "EOF_" + Guid.NewGuid().ToString("N");
        return $"npx --no-install eslint {Rule} --stdin --stdin-filename '{file}' <<'{eof}'\n{source.TrimEnd('\n')}\n{eof}";
    }
}
