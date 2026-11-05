using System.Globalization;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;

namespace Factory.Harness.Routing;

/// One line of `factory-s2 router-stats`: what the ledgers have taught the router about a model in a role and size.
/// Successes and failures are weighted by age (decay), so they are not whole numbers once evidence gets old.
public sealed record StatsRow(string Model, string Role, string Size, double Successes, double Failures, int Samples,
    double MeanTokensIn, double MeanTokensOut, decimal? CostPerSuccess);

public static class RouterStatsReport
{
    private static readonly string[] RoleNames = ["Planner", "TaskPlanner", "TestAuthor", "Implementer", "Reviewer", "Qa"];

    /// Cost per success = (mean tokens in * price in + mean tokens out * price out) / 1e6 / pass rate, at the catalog's
    /// current prices (placeholders until the APIM price import). Null when nothing has passed yet.
    public static IReadOnlyList<StatsRow> Rows(FSharpMap<StatsKey, Counts> stats, IReadOnlyList<ModelEntry> catalog, string? role)
    {
        string? want = null;
        if (role is not null)
            want = RoleNames.FirstOrDefault(n => n.Equals(role, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"'{role}' is not a role ({string.Join(", ", RoleNames)})");
        var prices = catalog.ToDictionary(m => m.Id, m => m.Price);
        return stats
            .Select(kv => (Key: kv.Key, Counts: kv.Value))
            .Where(x => want is null || x.Key.Role.ToString() == want)
            .Select(x =>
            {
                var total = x.Counts.Successes + x.Counts.Failures;
                decimal? cost = null;
                if (x.Counts.Successes > 0 && total > 0 && prices.TryGetValue(x.Key.Model, out var p))
                {
                    var perAttempt = ((decimal)x.Counts.MeanTokensIn * p.InPerMTok + (decimal)x.Counts.MeanTokensOut * p.OutPerMTok) / 1_000_000m;
                    cost = perAttempt / (decimal)(x.Counts.Successes / total);
                }
                return new StatsRow(x.Key.Model, x.Key.Role.ToString(), x.Key.Size.ToString(), x.Counts.Successes, x.Counts.Failures,
                    x.Counts.Samples, x.Counts.MeanTokensIn, x.Counts.MeanTokensOut, cost);
            })
            .OrderBy(r => r.Model, StringComparer.Ordinal).ThenBy(r => r.Role, StringComparer.Ordinal).ThenBy(r => r.Size, StringComparer.Ordinal)
            .ToList();
    }

    public static string Render(IReadOnlyList<StatsRow> rows, bool json)
    {
        if (json)
            return JsonSerializer.Serialize(rows.Select(r => new
            {
                model = r.Model, role = r.Role, size = r.Size, successes = r.Successes, failures = r.Failures, samples = r.Samples,
                mean_tokens_in = r.MeanTokensIn, mean_tokens_out = r.MeanTokensOut, cost_per_success = r.CostPerSuccess,
            })) + "\n";
        if (rows.Count == 0) return "no learned stats yet\n";
        var sb = new StringBuilder();
        string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        sb.AppendLine($"{"model",-24}{"role",-13}{"size",-5}{"succ",8}{"fail",8}{"n",5}{"tok in",9}{"tok out",9}  cost/success");
        foreach (var r in rows)
            sb.AppendLine($"{r.Model,-24}{r.Role,-13}{r.Size,-5}{F(r.Successes),8}{F(r.Failures),8}{r.Samples,5}{F(r.MeanTokensIn),9}{F(r.MeanTokensOut),9}  "
                + (r.CostPerSuccess is { } c ? c.ToString("0.000000", CultureInfo.InvariantCulture) : "-"));
        return sb.ToString();
    }
}
