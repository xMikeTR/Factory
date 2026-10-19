using Factory.Core;
using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Harness.Policy;
using Factory.Harness.Sandbox;
using Factory.Harness.Telemetry;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Quality;
using Factory.S2.Teams;
using Factory.S2.Verify;

namespace Factory.S2;

using Core = Factory.Core.Core;

/// Command-line parsing and the checks every command shares.
public static class CliOptions
{
    public static PlanValidation Validate(Dictionary<string, string> opts) =>
        Core.ValidatePlan(
            Core.ParseHandover(File.ReadAllText(Required(opts, "handover"))),
            File.ReadAllText(Required(opts, "plan")));

    /// Every `--reference key=path` (the flag repeats, which ParseOptions cannot carry).
    public static Factory.Harness.Registry.ReferenceBindings.Parsed ReferenceFlags(string[] a) =>
        Factory.Harness.Registry.ReferenceBindings.Parse(
            a.Select((x, i) => (x, i)).Where(t => t.x == "--reference" && t.i + 1 < a.Length).Select(t => a[t.i + 1]));

    /// A run whose handover names references starts only when each is bound to a folder outside the repo, and no other is.
    public static void RequireReferences(Handover handover, string[] a, string repoPath)
    {
        var flags = ReferenceFlags(a);
        var checkedRefs = Factory.Harness.Registry.ReferenceBindings.Check(handover.References, flags.Bindings, repoPath);
        var errors = flags.Errors.Concat(checkedRefs.Errors).ToList();
        if (errors.Count > 0) throw new FactoryRejectedException("read-only references rejected", errors.ToArray());
    }

    public static string Required(Dictionary<string, string> opts, string name) =>
        opts.TryGetValue(name, out var v) ? v : throw new ArgumentException($"--{name} is required");

    public static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            var key = a[i][2..];
            var hasValue = i + 1 < a.Length && !a[i + 1].StartsWith("--");
            d[key] = hasValue ? a[++i] : "true";
        }
        return d;
    }
}
