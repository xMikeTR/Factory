using System.Globalization;
using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;

namespace Factory.Harness.Spending;

/// What `factory-s2 budget-import` leaves in {stateDir}/budget/. Vendor-neutral: the files are S2's own format
/// (see schemas/budget-import.schema.json); whatever produced them is not S2's business.
///   prices.jsonl         append-only, one price row per (model, effective date)
///   spend-YYYY-MM.json   the latest import's spend rows for that month (a re-import replaces the month)
///   last-import.json     {"at": ...}: when the latest import ran (the stale-import rule reads it)
public static class BudgetStore
{
    public sealed record ImportResult(IReadOnlyList<string> Errors, int PricesAdded, int SpendRows);

    private static string Dir(string stateDir) => Path.Combine(stateDir, "budget");

    /// Parses both texts (either may be null) and writes only when both are valid.
    public static async Task<ImportResult> ImportAsync(string stateDir, string? pricesText, string? spendText, DateTimeOffset now, CancellationToken ct)
    {
        var errors = new List<string>();
        List<PriceRow> prices = [];
        List<SpendRow> spend = [];
        if (pricesText is not null)
        {
            var r = BudgetImport.parsePrices(pricesText);
            if (r.IsError) errors.AddRange(r.ErrorValue.Select(e => "prices: " + e)); else prices = [.. r.ResultValue];
        }
        if (spendText is not null)
        {
            var r = BudgetImport.parseSpend(spendText);
            if (r.IsError) errors.AddRange(r.ErrorValue.Select(e => "spend: " + e)); else spend = [.. r.ResultValue];
        }
        if (errors.Count > 0) return new ImportResult(errors, 0, 0);

        Directory.CreateDirectory(Dir(stateDir));
        var existing = (await LoadPricesAsync(stateDir, ct)).Select(p => (p.Model, p.Effective)).ToHashSet();
        var added = prices.DistinctBy(p => (p.Model, p.Effective)).Where(p => !existing.Contains((p.Model, p.Effective))).ToList();
        if (added.Count > 0)
            await File.AppendAllLinesAsync(Path.Combine(Dir(stateDir), "prices.jsonl"), added.Select(ToJson), ct);

        foreach (var month in spend.GroupBy(s => s.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
            await File.WriteAllTextAsync(Path.Combine(Dir(stateDir), $"spend-{month.Key}.json"),
                JsonSerializer.Serialize(month.Select(s => new { date = s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), model = s.Model, tokens_in = s.TokensIn, tokens_out = s.TokensOut, cost = s.Cost })), ct);

        await File.WriteAllTextAsync(Path.Combine(Dir(stateDir), "last-import.json"), JsonSerializer.Serialize(new { at = now }), ct);
        return new ImportResult([], added.Count, spend.Count);
    }

    private static string ToJson(PriceRow p) => JsonSerializer.Serialize(new
    {
        model = p.Model, in_per_mtok = p.InPerMTok, out_per_mtok = p.OutPerMTok, currency = p.Currency, source = p.Source,
        effective = p.Effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    });

    public static async Task<IReadOnlyList<PriceRow>> LoadPricesAsync(string stateDir, CancellationToken ct)
    {
        var path = Path.Combine(Dir(stateDir), "prices.jsonl");
        if (!File.Exists(path)) return [];
        var rows = new List<PriceRow>();
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                rows.Add(new PriceRow(r.GetProperty("model").GetString()!, r.GetProperty("in_per_mtok").GetDecimal(), r.GetProperty("out_per_mtok").GetDecimal(),
                    r.GetProperty("currency").GetString()!, r.GetProperty("source").GetString()!, DateOnly.ParseExact(r.GetProperty("effective").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException) { /* a damaged line is skipped */ }
        }
        return rows;
    }

    /// The month's total cost from the latest import, or null when that month was never imported.
    public static async Task<decimal?> UserMonthSpendAsync(string stateDir, string month, CancellationToken ct)
    {
        var path = Path.Combine(Dir(stateDir), $"spend-{month}.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            return doc.RootElement.EnumerateArray().Sum(e => e.GetProperty("cost").GetDecimal());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static async Task<DateTimeOffset?> LastImportAsync(string stateDir, CancellationToken ct)
    {
        var path = Path.Combine(Dir(stateDir), "last-import.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            return doc.RootElement.GetProperty("at").GetDateTimeOffset();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException) { return null; }
    }

    /// Catalog rows with the imported price in force on `on` (matched by ModelName, then Id). A model with no
    /// imported price keeps its placeholder and is listed in `unmatched` (router-stats shows them).
    public static (IReadOnlyList<ModelEntry> Models, IReadOnlyList<string> Unmatched) ApplyPrices(
        IEnumerable<ModelEntry> catalog, IReadOnlyList<PriceRow> prices, DateOnly on)
    {
        var list = ListModule.OfSeq(prices);
        var models = new List<ModelEntry>();
        var unmatched = new List<string>();
        foreach (var m in catalog)
        {
            var p = BudgetImport.priceAt(list, m.ModelName, on) ?? BudgetImport.priceAt(list, m.Id, on);
            if (p is null) { models.Add(m); unmatched.Add(m.Id); }
            else models.Add(new ModelEntry(m.Id, m.Family, m.Protocol, m.ModelName, m.Tier, new Price(p.Value.InPerMTok, p.Value.OutPerMTok),
                m.Caps, m.ContextWindow, m.ApprovedFor, m.Prior));
        }
        return (models, unmatched);
    }

    /// Tokens times the price in force on the date; null when the model has no price then (old rows stay cost-less).
    public static decimal? CostOf(IReadOnlyList<PriceRow> prices, string model, long tokensIn, long tokensOut, DateOnly on) =>
        BudgetImport.priceAt(ListModule.OfSeq(prices), model, on) is { } p ? BudgetImport.cost(p.Value, tokensIn, tokensOut) : null;
}
