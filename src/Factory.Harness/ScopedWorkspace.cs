using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness.Sandbox;
using Microsoft.Extensions.AI;

namespace Factory.Harness;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// What an agent reported during its turn, through tools rather than free text, so the runner
/// never has to parse prose to decide what happens next.
public sealed class TurnSignals
{
    public string? DoneSummary { get; set; }
    public RaisedDecision? NeedsDecision { get; set; }
    public string? ReturnedToTestAuthor { get; set; }
    /// The turn hit its tool-call cap and was stopped by the harness.
    public bool Capped { get; set; }
    /// The task-planner's approach note and the context selection that survived validation (submit_approach).
    public string? ApproachNote { get; set; }
    public Selection? ContextSelection { get; set; }
    /// The reviewer's findings as submitted (submit_review); null if it never called the tool.
    public IReadOnlyList<Finding>? Review { get; set; }
    /// The last plan the planner passed to submit_plan, as JSON, whatever the validator said about it.
    public string? SubmittedPlan { get; set; }
    /// The repro agent's steps (submit_reproduction) and the diagnosis agent's finding (submit_diagnosis); null if never called.
    public string? Reproduction { get; set; }
    public string? Diagnosis { get; set; }
    /// The UX judge's scores for the screenshot it was shown (submit_ux_scores); null if never recorded.
    public UxScore? UxScore { get; set; }
    public List<string> FilesWritten { get; } = [];
    /// The QA author's tests (submit_qa_tests); null if it never called the tool.
    public IReadOnlyList<QaTestDecl>? QaTests { get; set; }
    /// Mutants the TestAuthor declared equivalent (declare_equivalent): id -> why no test could tell it from the original.
    public Dictionary<string, string> DeclaredEquivalents { get; } = [];
    /// Mutant ids the reviewer accepted as equivalent (confirm_equivalents); null if it never called the tool.
    public IReadOnlySet<string>? ConfirmedEquivalents { get; set; }
}

/// One QA test as the author declares it: which requirement it pins, whether it is new behaviour or a characterisation, and the command that runs it.
public sealed record QaTestArg(
    [property: Description("a short unique id, letters digits and dashes")] string Id,
    [property: Description("the requirement this test pins, copied exactly from the list you were given")] string Requirement,
    [property: Description("new = behaviour this run adds (fails before, passes after); characterisation = behaviour that must not change (passes on both)")] string Kind,
    [property: Description("the command that runs this one test from the repository root and exits 0 only if it passes")] string Command);

public sealed record QaTestDecl(string Id, string Requirement, bool IsNew, string Command);

/// One file the task-planner nominates for the context pack.
public sealed record ContextArg(
    [property: Description("repo-relative path of an existing file")] string Path,
    [property: Description("one line: why the test author or implementer needs it")] string Reason);

/// One finding of submit_review, as the reviewer sends it.
public sealed record FindingArg(
    [property: Description("test_gaming | intent | bound_decision | security | weak_tests | design (design is advice and never blocks)")] string Category,
    [property: Description("repo-relative path of a file in the diff")] string File,
    [property: Description("a line of that file shown in the diff (new-side line number)")] int Line,
    [property: Description("what is wrong and what would fix it, in one or two sentences")] string Message);

/// A question raised through raise_needs_decision, with the options the designer chooses between.
public sealed record RaisedDecision(string Question, IReadOnlyList<DecisionOption> Options, string? Recommended);

/// One option of raise_needs_decision, as the agent sends it.
public sealed record OptionArg(
    [property: Description("a single capital letter: A, B, C...")] string Id,
    [property: Description("the option in one line")] string Summary,
    [property: Description("what choosing it means for the code, the tests or the design")] string Consequence);

/// The file system as one task sees it. Writes are checked against the task's `writes` globs and
/// the handover's protected paths *here*, independent of the policy engine: defence in depth, and
/// the reason the git scope check after each turn should normally find nothing.
public sealed class ScopedWorkspace(
    string root,
    IReadOnlyList<PathPattern> writes,
    IReadOnlyList<PathPattern> protectedPaths,
    IReadOnlyList<string> commandAllowList,
    TurnSignals signals,
    ICommandSandbox? sandbox = null,
    Func<CancellationToken, Task<string>>? qualityCheck = null,
    IReadOnlyDictionary<string, string>? references = null,
    TimeSpan? commandCap = null)
{
    private readonly string _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private const int MaxReadChars = 200_000;

    /// The planner's tools: read the repo, and submit_plan, whose input schema is the plan schema and
    /// which answers with the validator's errors so the planner can fix them inside one turn.
    public IList<AITool> PlannerTools(JsonElement planSchema, Func<string, IReadOnlyList<string>> validate) =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        new SubmitPlanFunction(planSchema, validate, signals),
    ];

    public IList<AITool> ReadOnlyTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(RaiseNeedsDecision, "raise_needs_decision"),
    ];

    /// The task-planner's tools: read-only, a needs-decision, and submit_approach, which records the
    /// approach note and nominates context files. `select` validates the nominations; rejections
    /// are reported back and never fail the turn.
    public IList<AITool> TaskPlannerTools(Func<IReadOnlyList<Nomination>, Selection> select) =>
    [
        .. ReadOnlyTools(),
        AIFunctionFactory.Create(
            ([Description("the approach note, under 300 words")] string note,
             [Description("files the implementer and test author should see in full, each with the reason")] ContextArg[] context) =>
            {
                var selection = select((context ?? []).Select(c => new Nomination(c.Path ?? "", c.Reason ?? "")).ToList());
                signals.ApproachNote = note;
                signals.ContextSelection = selection;
                return selection.Rejected.IsEmpty
                    ? "Recorded. Stop now."
                    : "Recorded. These nominations were not used: " + string.Join("; ", selection.Rejected.Select(r => $"{r.Item1} ({r.Item2})")) + ". Stop now.";
            }, "submit_approach", "Call once, last, instead of replying in prose: the approach note, plus the existing files the test author and implementer need to see. Ends your turn."),
    ];

    /// The reviewer's tools: read the repo, and submit_review, which ends the review. No writes, no
    /// commands, no way to park: a reviewer that cannot judge submits nothing and the harness parks.
    public IList<AITool> ReviewerTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(
            ([Description("every problem you found, each citing a line in the diff; empty if there is none")] FindingArg[] findings) =>
            {
                var parsed = new List<Finding>();
                var errors = new List<string>();
                foreach (var f in findings ?? [])
                {
                    if (FindingCategories.TryGetValue(f.Category ?? "", out var category)) parsed.Add(new Finding(category, f.File ?? "", f.Line, f.Message ?? ""));
                    else errors.Add($"{f.File}:{f.Line}: category '{f.Category}' is not one of {string.Join(", ", FindingCategories.Keys)}");
                }
                if (errors.Count > 0) return "Not recorded. Fix every one and call again:\n- " + string.Join("\n- ", errors);
                signals.Review = parsed;
                return "Recorded. Stop now.";
            }, "submit_review", "Call once, last: your findings (an empty list approves). Ends your turn."),
    ];

    private static readonly IReadOnlyDictionary<string, FindingCategory> FindingCategories = new Dictionary<string, FindingCategory>
    {
        ["test_gaming"] = FindingCategory.TestGaming,
        ["intent"] = FindingCategory.Intent,
        ["bound_decision"] = FindingCategory.BoundDecision,
        ["security"] = FindingCategory.Security,
        ["weak_tests"] = FindingCategory.WeakTests,
        ["design"] = FindingCategory.Design,
    };

    /// The reviewer's tools when asked about equivalent mutants: read the repo, and confirm_equivalents, which ends the turn.
    public IList<AITool> EquivalenceReviewerTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(
            ([Description("the ids of the claims you accept as truly equivalent; leave out every one you doubt")] string[] confirmed) =>
            {
                signals.ConfirmedEquivalents = (confirmed ?? []).ToHashSet();
                return "Recorded. Stop now.";
            }, "confirm_equivalents", "Call once, last: the mutant ids you accept as equivalent. Ends your turn."),
    ];

    /// The TestAuthor's tools: the executor's, plus declaring a surviving mutant equivalent.
    public IList<AITool> TestAuthorTools() =>
    [
        .. ExecutorTools(),
        AIFunctionFactory.Create(DeclareEquivalent, "declare_equivalent"),
    ];

    [Description("Claim a surviving mutant is equivalent: no test could tell it from the original code, so it is not a weakness of your tests. "
        + "A reviewer must accept the claim; a claim that is wrong costs you the task, so do not use this to avoid writing a test.")]
    public string DeclareEquivalent([Description("the mutant id from the survivor list")] string mutantId, [Description("why every input behaves the same")] string reason)
    {
        if (string.IsNullOrWhiteSpace(mutantId) || string.IsNullOrWhiteSpace(reason)) return "Not recorded: give the mutant id and the reason.";
        signals.DeclaredEquivalents[mutantId.Trim()] = reason.Trim();
        return "Recorded. A reviewer will judge it; carry on with the tests that are still needed.";
    }

    /// The QA author's tools: read the repo, write tests (inside the paths this workspace allows), run commands in the sandbox,
    /// and submit_qa_tests, which declares each test with its requirement, kind and command and ends the turn.
    public IList<AITool> QaAuthorTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(WriteFile, "write_file"),
        AIFunctionFactory.Create(RunCommand, "run_command"),
        AIFunctionFactory.Create(
            ([Description("every test, each pinning one requirement")] QaTestArg[] tests) =>
            {
                var errors = new List<string>();
                var parsed = new List<QaTestDecl>();
                foreach (var t in tests ?? [])
                {
                    if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Requirement) || string.IsNullOrWhiteSpace(t.Command)) errors.Add($"{t.Id}: id, requirement and command are all required");
                    else if (t.Kind is not ("new" or "characterisation")) errors.Add($"{t.Id}: kind '{t.Kind}' is not new|characterisation");
                    else if (parsed.Any(p => p.Id == t.Id)) errors.Add($"{t.Id}: duplicate id");
                    else parsed.Add(new QaTestDecl(t.Id, t.Requirement, t.Kind == "new", t.Command));
                }
                if (errors.Count > 0) return "Not recorded. Fix every one and call again:\n- " + string.Join("\n- ", errors);
                signals.QaTests = parsed;
                return "Recorded. Stop now.";
            }, "submit_qa_tests", "Call once, last: your tests. Ends your turn."),
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
        .. ReferenceTools(),
    ];

    /// Read-only references (a legacy app being replaced): offered only when the run has some. No tool writes through them.
    private AITool[] ReferenceTools() => references is { Count: > 0 }
        ? [AIFunctionFactory.Create(ReadReference, "read_reference"), AIFunctionFactory.Create(ListReference, "list_reference")]
        : [];

    /// The repro agent's tools: read the repo, drive a browser, submit the steps that reproduce the escape.
    /// It writes no file: the failing spec reaches `tests/acceptance/**` only through intake.
    public IList<AITool> ReproTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(PlaywrightCli, "playwright_cli"),
        AIFunctionFactory.Create(
            ([Description("the steps, in order, that reproduce the reported behaviour, with what you saw")] string steps) =>
            {
                signals.Reproduction = steps;
                return "Recorded. Stop now.";
            }, "submit_reproduction", "Call once, last: the reproduction steps. Ends your turn."),
    ];

    /// The UX judge's only tool: its scores for the one screenshot in the message.
    public IList<AITool> UxJudgeTools() =>
    [
        AIFunctionFactory.Create(
            ([Description("1 to 5: does the eye land on the primary content and action first")] int hierarchy,
             [Description("1 to 5: does every control and message say what it does")] int clarity,
             [Description("1 to 5: does it match the rest of the design's spacing, type and controls")] int consistency,
             [Description("one sentence naming the single biggest issue, or 'none'")] string note) =>
            {
                var parsed = UxReview.parseScore(hierarchy, clarity, consistency, note);
                if (parsed.IsError) return "Not recorded: " + parsed.ErrorValue + ". Call again.";
                signals.UxScore = parsed.ResultValue;
                return "Recorded. Stop now.";
            }, "submit_ux_scores", "Call once, last: your scores for this screenshot. Ends your turn."),
    ];

    /// The diagnosis agent's tools: read-only, and the finding goes to the ledger through submit_diagnosis.
    public IList<AITool> DiagnosisTools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(PlaywrightCli, "playwright_cli"),
        AIFunctionFactory.Create(
            ([Description("why the frozen spec failed on this element, in a few sentences, citing what you saw")] string finding) =>
            {
                signals.Diagnosis = finding;
                return "Recorded. Stop now.";
            }, "submit_diagnosis", "Call once, last: your finding. Ends your turn."),
    ];

    /// The Implementer's tools: the executor's, plus handing broken tests back to the TestAuthor
    /// and, when the run has a browser CLI (`selfCheck`), a way to poke its own UI before the gate.
    public IList<AITool> ImplementerTools(bool selfCheck = false) =>
    [
        .. ExecutorTools(),
        .. selfCheck ? new[] { AIFunctionFactory.Create(PlaywrightCli, "playwright_cli") } : [],
        AIFunctionFactory.Create(ReturnToTestAuthor, "return_to_test_author"),
        .. qualityCheck is null ? [] : new[] { AIFunctionFactory.Create(CheckQuality, "check_quality") },
    ];

    [Description("Check your changes so far against the project's quality thresholds (complexity, length). Static and fast; "
        + "it does not count toward your tool-call limit. Run it before declare_done.")]
    public async Task<string> CheckQuality(CancellationToken ct = default) =>
        qualityCheck is null ? "quality is not measured in this run" : await qualityCheck(ct) is { Length: > 0 } found ? found : "no quality findings";

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
            .Where(f => !IsHidden(f) && !f.StartsWith(".factory/"))
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

    /// How long one agent command may take. Anthropic's prompt cache lasts 5 minutes from the last model call
    /// (PromptCache uses the default lifetime), so this leaves room for the call before and after.
    public static readonly TimeSpan DefaultCommandCap = TimeSpan.FromMinutes(3);

    private static string TimedOut(TimeSpan cap, string tail) =>
        $"exit 124 (timed out after {cap.TotalSeconds:0} s, including any wait for a free slot)\n{tail}\n"
        + "Commands that long are not run here: declare_done runs the verify checks outside your turn. Run a narrower command, or declare_done.";

    [Description("Run an allow-listed build or test command from the repository root, e.g. 'dotnet test tests/X'. Returns exit code and the output tail.")]
    public async Task<string> RunCommand([Description("the command line")] string command, CancellationToken ct = default)
    {
        var cmd = command.Trim();
        if (!commandAllowList.Any(prefix => cmd == prefix || cmd.StartsWith(prefix + " ", StringComparison.Ordinal)))
            return $"DENIED: only these commands are allowed: {string.Join(", ", commandAllowList)}";
        if (cmd.IndexOfAny([';', '&', '|', '`', '$', '>', '<']) >= 0)
            return "DENIED: shell operators are not allowed; run one command at a time.";

        // Never on the host environment: the gateway key and tokens must not reach agent commands.
        if (sandbox is null)
            return "DENIED: no sandbox is attached to this workspace, so commands cannot run.";

        // One deadline for the whole call, queueing for a sandbox slot and a flaky re-run included: the prompt cache
        // goes cold if this call outlasts it, and the next model call then pays for the whole conversation again.
        var cap = commandCap ?? DefaultCommandCap;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(cap);
        try
        {
            var result = await sandbox.RunAsync(cmd, _root, cap, new Dictionary<string, string>(), deadline.Token);
            return result.TimedOut ? TimedOut(cap, result.Tail(6000)) : $"exit {result.ExitCode}\n{result.Tail(6000)}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TimedOut(cap, "");
        }
    }

    /// Subcommands of `playwright-cli` an agent may run: drive and observe a page, nothing that installs, runs
    /// arbitrary code or writes outside the snapshot directory.
    public static readonly IReadOnlySet<string> PlaywrightSubcommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "open", "goto", "snapshot", "click", "dblclick", "fill", "type", "press", "check", "uncheck", "select",
        "hover", "screenshot", "resize", "console", "network", "tab-list", "close",
    };

    [Description("Drive the task's own dev server in a browser with the Playwright CLI, e.g. 'open http://localhost:3000', 'snapshot', "
        + "'click e21'. Only localhost addresses; snapshots land under .playwright-cli/. Returns exit code and the output tail.")]
    public async Task<string> PlaywrightCli([Description("the playwright-cli arguments, without the program name")] string command, CancellationToken ct = default)
    {
        var args = command.Trim();
        if (args.IndexOfAny([';', '&', '|', '`', '$', '>', '<', '\n']) >= 0)
            return "DENIED: shell operators are not allowed; run one command at a time.";
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !PlaywrightSubcommands.Contains(words[0]))
            return $"DENIED: only these subcommands are allowed: {string.Join(", ", PlaywrightSubcommands.Order(StringComparer.Ordinal))}";
        foreach (var w in words.Skip(1).Where(w => w.Contains("://", StringComparison.Ordinal)))
            if (!Uri.TryCreate(w, UriKind.Absolute, out var uri) || uri.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
                return $"DENIED: {w} is not a localhost address; browser sessions run against this worktree's dev server only.";
        if (sandbox is null)
            return "DENIED: no sandbox is attached to this workspace, so commands cannot run.";

        var result = await sandbox.RunAsync("playwright-cli " + args, _root, TimeSpan.FromMinutes(2), new Dictionary<string, string>(), ct);
        return $"exit {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}\n{result.Tail(6000)}";
    }

    [Description("Call when you believe the task's verify checks will now pass. Ends your turn; the checks run next.")]
    public string DeclareDone([Description("one paragraph: what you changed and why it satisfies the task")] string summary)
    {
        signals.DoneSummary = summary;
        return "Recorded. Stop now; verification runs next.";
    }

    [Description("Call instead of guessing when the task needs a design decision the handover does not contain (a limit, an owner, a behaviour). "
        + "Offer the options you see, each with its consequence; the designer picks one. Ends your turn.")]
    public string RaiseNeedsDecision(
        [Description("one precise question a designer can answer")] string question,
        [Description("at least two options, ids A, B, C...")] OptionArg[] options,
        [Description("the id of the option you would choose, if any")] string? recommended = null)
    {
        var opts = (options ?? []).Select(o => new DecisionOption(o.Id ?? "", o.Summary ?? "", o.Consequence ?? "")).ToList();
        var rec = string.IsNullOrWhiteSpace(recommended) ? null : recommended;
        var errors = Core.DecisionOptionErrors(opts, rec!).ToList();
        if (errors.Count > 0)
            return "Not recorded. Fix every one and call again:\n- " + string.Join("\n- ", errors);
        signals.NeedsDecision = new RaisedDecision(question, opts, rec);
        return "Recorded. Stop now; the question goes to System 1.";
    }

    [Description("Call when the tests or verify checks are broken in a way only the test author can fix: a test file, test project or fixture outside your write paths, or a test that contradicts the brief. Not for design questions. Ends your turn.")]
    public string ReturnToTestAuthor([Description("what is broken, where, and why your write paths cannot fix it")] string problem)
    {
        signals.ReturnedToTestAuthor = problem;
        return "Recorded. Stop now; the test author fixes the tests, then the task comes back to you.";
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

    [Description("Read a UTF-8 text file from a read-only reference (named in the brief). Path is relative to that reference's root. You can never write there.")]
    public string ReadReference([Description("the reference key from the brief")] string key, [Description("path inside the reference")] string path)
    {
        if (!TryResolveReference(key, path, out var full, out var error)) return error;
        if (!File.Exists(full)) return $"ERROR: {path} does not exist in reference {key}";
        var text = File.ReadAllText(full);
        return text.Length <= MaxReadChars ? text : text[..MaxReadChars] + "\n…(truncated)";
    }

    [Description("List files under a directory of a read-only reference, recursively, excluding .git, bin and obj. Max 500 entries.")]
    public string ListReference([Description("the reference key from the brief")] string key, [Description("directory inside the reference, '.' for its root")] string directory)
    {
        if (!TryResolveReference(key, directory, out var full, out var error)) return error;
        if (!Directory.Exists(full)) return $"ERROR: {directory} is not a directory in reference {key}";
        var root = references![key].TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsHidden(f))
            .Order(StringComparer.Ordinal)
            .Take(501)
            .ToList();
        return string.Join("\n", files.Take(500)) + (files.Count > 500 ? "\n…(more than 500 files; narrow the directory)" : "");
    }

    /// The path must stay inside the reference after symlinks are followed, so a link in a legacy repo cannot lead out of it.
    private bool TryResolveReference(string key, string path, out string full, out string error)
    {
        full = "";
        if (references is null || !references.TryGetValue(key, out var rootPath))
        {
            error = $"ERROR: no reference '{key}'" + (references is { Count: > 0 } ? $"; the keys are {string.Join(", ", references.Keys.Order())}" : "");
            return false;
        }
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(path)) { error = $"DENIED: {path} is outside reference {key}"; return false; }
        var rel = Normalise(path);
        full = Path.GetFullPath(Path.Combine(root, rel));
        var ok = (full + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.Ordinal)
                 && (RealPath(full) + Path.DirectorySeparatorChar).StartsWith(RealRoot(root), StringComparison.Ordinal)
                 && rel != ".git" && !rel.StartsWith(".git/");
        error = ok ? "" : $"DENIED: {path} is outside reference {key}";
        return ok;
    }

    /// Where `full` ends up after every link on its way is followed, component by component. Components that do not exist yet are kept as written.
    internal static string RealPath(string full)
    {
        var path = Path.GetFullPath(full);
        var cur = Path.GetPathRoot(path)!;
        foreach (var part in path[cur.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, part);
            FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) cur = target.FullName;
        }
        return cur;
    }

    private static string RealRoot(string root) =>
        RealPath(root.TrimEnd(Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private bool TryResolve(string path, out string full, out string error)
    {
        full = Path.GetFullPath(Path.Combine(_root, Normalise(path)));
        var ok = (full + Path.DirectorySeparatorChar).StartsWith(_root, StringComparison.Ordinal) &&
                 !Normalise(path).StartsWith(".git/") && Normalise(path) != ".git";
        // A link inside the worktree must not lead out of it: judge where the path really points.
        ok = ok && (RealPath(full) + Path.DirectorySeparatorChar).StartsWith(RealRoot(_root), StringComparison.Ordinal);
        error = ok ? "" : $"DENIED: {path} is outside the workspace";
        return ok;
    }

    /// A listing leaves out .git and build output at any depth, the root included (a folder named bin, obj or node_modules).
    private static bool IsHidden(string rel) =>
        rel.Split('/')[..^1].Any(d => d is ".git" or "bin" or "obj" or "node_modules");

    private static string Normalise(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        return p.StartsWith("./") ? p[2..] : p.TrimStart('/');
    }
}

/// submit_plan: the plan is the tool call's arguments, shaped by the plan schema, so the model never
/// writes JSON as free text (live5: a raw newline inside a string cost a whole planner turn).
internal sealed class SubmitPlanFunction(JsonElement planSchema, Func<string, IReadOnlyList<string>> validate, TurnSignals signals) : AIFunction
{
    public const int MaxSubmissions = 3;
    private int _submissions;

    public override string Name => "submit_plan";
    public override string Description =>
        "Submit the whole implementation plan. It is validated at once: you get back either Accepted (end your turn) " +
        $"or the rules it breaks (fix every one and submit the whole plan again). At most {MaxSubmissions} submissions.";

    public override JsonElement JsonSchema { get; } = WithoutMeta(planSchema);

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (++_submissions > MaxSubmissions)
            return new($"DENIED: {MaxSubmissions} submissions is the limit for one turn. End your turn.");
        var plan = JsonSerializer.Serialize(arguments.ToDictionary(a => a.Key, a => a.Value));
        signals.SubmittedPlan = plan;
        var errors = validate(plan);
        return new(errors.Count == 0
            ? "Accepted: the plan passed validation. End your turn."
            : "Rejected. Fix every one of these and call submit_plan again with the whole plan:\n- " + string.Join("\n- ", errors));
    }

    private static JsonElement WithoutMeta(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node.Remove("$schema");
        node.Remove("$id");
        return JsonSerializer.SerializeToElement(node);
    }
}
