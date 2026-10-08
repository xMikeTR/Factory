using System.Text;
using System.Text.Json;

namespace Factory.S2.Quality;

/// The PR-body section for npm install scripts the restore skipped (`--ignore-scripts`) and the policy did not allow.
/// A build that needs one is a decision for the repo's owners, so it is reported, not run.
public static class InstallScriptReport
{
    public static string? Section(IEnumerable<string> restorePayloads)
    {
        var skipped = restorePayloads
            .Select(p => JsonDocument.Parse(p).RootElement)
            .Where(r => r.TryGetProperty("skipped_install_scripts", out _))
            .SelectMany(r => r.GetProperty("skipped_install_scripts").EnumerateArray().Select(e => e.GetString()!))
            .Distinct().Order().ToList();
        if (skipped.Count == 0) return null;
        var sb = new StringBuilder("### Install scripts\n");
        sb.AppendLine("These packages have an install script that was not run (`npm ci --ignore-scripts`). If the build needs one, list the package under `npm.rebuild` in `.factory/policy.yaml`:");
        foreach (var s in skipped) sb.AppendLine($"- {s}");
        return sb.ToString();
    }
}
