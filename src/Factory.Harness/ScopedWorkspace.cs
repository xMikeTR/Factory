using System.ComponentModel;
using Factory.Core;
using Microsoft.Extensions.AI;

namespace Factory.Harness;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// What an agent reported during its turn, through tools rather than free text, so the runner
/// never has to parse prose to decide what happens next.
public sealed class TurnSignals
{
    public string? DoneSummary { get; set; }
    public string? NeedsDecision { get; set; }
    public List<string> FilesWritten { get; } = [];
}

/// The file system as one task sees it. Writes are checked against the task's `writes` globs and
/// the handover's protected paths *here*, independent of the policy engine: defence in depth, and
/// the reason the git scope check after each turn should normally find nothing.
public sealed class ScopedWorkspace(
    string root,
    IReadOnlyList<PathPattern> writes,
    IReadOnlyList<PathPattern> protectedPaths,
    IReadOnlyList<string> commandAllowList,
    TurnSignals signals)
{
    private readonly string _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private const int MaxReadChars = 200_000;

    public IList<AITool> PlannerTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
    ];

    public IList<AITool> ReadOnlyTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(RaiseNeedsDecision, "raise_needs_decision"),
    ];

    public IList<AITool> ExecutorTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(WriteFile, "write_file"),
        AIFunctionFactory.Create(DeleteFile, "delete_file"),
        AIFunctionFactory.Create(RunCommand, "run_command"),
        AIFunctionFactory.Create(DeclareDone, "declare_done"),
        AIFunctionFactory.Create(RaiseNeedsDecision, "raise_needs_decision"),
    ];

    [Description("Read a UTF-8 text file. Path is relative to the repository root.")]
    public string ReadFile([Description("repo-relative path")] string path)
    {
        if (!TryResolve(path, out var full, out var error)) return error;
        if (!File.Exists(full)) return $"ERROR: {path} does not exist";
        var text = File.ReadAllText(full);
        return text.Length <= MaxReadChars ? text : text[..MaxReadChars] + "\n…(truncated)";
    }

    [Description("List files under a directory, recursively, excluding .git, bin and obj. Max 500 entries.")]
    public string ListFiles([Description("repo-relative directory, '.' for root")] string directory)
    {
        if (!TryResolve(directory, out var full, out var error)) return error;
        if (!Directory.Exists(full)) return $"ERROR: {directory} is not a directory";
        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/") && !f.Contains("/bin/") && !f.Contains("/obj/") && !f.StartsWith(".factory/"))
            .Order(StringComparer.Ordinal)
            .Take(501)
            .ToList();
        return string.Join("\n", files.Take(500)) + (files.Count > 500 ? "\n…(more than 500 files; narrow the directory)" : "");
    }

    [Description("Create or overwrite a file. Only paths inside this task's declared write paths are allowed.")]
    public string WriteFile([Description("repo-relative path")] string path, [Description("full new file content")] string content)
    {
        if (!TryResolve(path, out var full, out var error)) return error;
        if (Refusal(path) is { } refusal) return refusal;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        signals.FilesWritten.Add(Normalise(path));
        return $"wrote {path} ({content.Length} chars)";
    }

    [Description("Delete a file inside this task's declared write paths.")]
    public string DeleteFile([Description("repo-relative path")] string path)
    {
        if (!TryResolve(path, out var full, out var error)) return error;
        if (Refusal(path) is { } refusal) return refusal;
        if (!File.Exists(full)) return $"ERROR: {path} does not exist";
        File.Delete(full);
        signals.FilesWritten.Add(Normalise(path));
        return $"deleted {path}";
    }

    [Description("Run an allow-listed build or test command from the repository root, e.g. 'dotnet test tests/X'. Returns exit code and the output tail.")]
    public async Task<string> RunCommand([Description("the command line")] string command, CancellationToken ct = default)
    {
        var cmd = command.Trim();
        if (!commandAllowList.Any(prefix => cmd == prefix || cmd.StartsWith(prefix + " ", StringComparison.Ordinal)))
            return $"DENIED: only these commands are allowed: {string.Join(", ", commandAllowList)}";
        if (cmd.IndexOfAny([';', '&', '|', '`', '$', '>', '<']) >= 0)
            return "DENIED: shell operators are not allowed; run one command at a time.";

        var result = await ProcessRunner.ShellAsync(cmd, _root, TimeSpan.FromMinutes(10), ct: ct);
        return $"exit {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}\n{result.Tail(6000)}";
    }

    [Description("Call when you believe the task's verify checks will now pass. Ends your turn; the checks run next.")]
    public string DeclareDone([Description("one paragraph: what you changed and why it satisfies the task")] string summary)
    {
        signals.DoneSummary = summary;
        return "Recorded. Stop now; verification runs next.";
    }

    [Description("Call instead of guessing when the task needs a design decision the handover does not contain (a limit, an owner, a behaviour). Ends your turn.")]
    public string RaiseNeedsDecision([Description("one precise question a designer can answer")] string question)
    {
        signals.NeedsDecision = question;
        return "Recorded. Stop now; the question goes to System 1.";
    }

    private string? Refusal(string path)
    {
        var rel = Normalise(path);
        if (protectedPaths.Any(p => Core.Matches(p, rel)))
            return $"DENIED: {rel} is a protected path (registry, CI, or verify scripts).";
        if (!writes.Any(w => Core.Matches(w, rel)))
            return $"DENIED: {rel} is outside this task's write paths: {string.Join(", ", writes.Select(w => w.Value))}. " +
                   "If the task genuinely needs it, raise_needs_decision.";
        return null;
    }

    private bool TryResolve(string path, out string full, out string error)
    {
        full = Path.GetFullPath(Path.Combine(_root, Normalise(path)));
        var ok = (full + Path.DirectorySeparatorChar).StartsWith(_root, StringComparison.Ordinal) &&
                 !Normalise(path).StartsWith(".git/") && Normalise(path) != ".git";
        error = ok ? "" : $"DENIED: {path} is outside the workspace";
        return ok;
    }

    private static string Normalise(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        return p.StartsWith("./") ? p[2..] : p.TrimStart('/');
    }
}
