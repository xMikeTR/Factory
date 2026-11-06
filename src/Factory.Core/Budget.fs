namespace Factory.Core

open System
open System.Globalization
open System.Text.Json

/// One model's price from the import: per million tokens, effective from a date. Append-only, so a turn is costed
/// with the price in force on its own date.
type PriceRow =
    { Model: string
      InPerMTok: decimal
      OutPerMTok: decimal
      Currency: string
      Source: string
      Effective: DateOnly }

/// One day's spend of one model, as the import supplies it (the user budget reads these).
type SpendRow =
    { Date: DateOnly
      Model: string
      TokensIn: int64
      TokensOut: int64
      Cost: decimal }

/// Where the spend ratio turns Soft and Downgrade (fractions of the limit, 0 < softAt <= downgradeAt <= 1).
type BudgetLimits = { SoftAt: float; DowngradeAt: float }

/// The vendor-neutral import format: S2 defines it, a converter at the source writes it. Prices and spend are each a
/// CSV (header row, columns by name) or a JSON array of objects with the same names. See schemas/budget-import.schema.json.
[<RequireQualifiedAccess>]
module BudgetImport =
    let private inv = CultureInfo.InvariantCulture

    /// Rows as column -> text, from CSV (first line is the header) or a JSON array. Errors name the line (CSV) or index (JSON).
    let private records (text: string) : Result<(int * Map<string, string>) list, string list> =
        let t = text.TrimStart()
        if t.StartsWith "[" then
            try
                use doc = JsonDocument.Parse t
                [ for i, el in doc.RootElement.EnumerateArray() |> Seq.indexed ->
                      i + 1,
                      [ for p in el.EnumerateObject() ->
                            p.Name, (if p.Value.ValueKind = JsonValueKind.String then p.Value.GetString() else p.Value.GetRawText()) ]
                      |> Map.ofList ]
                |> Ok
            with :? JsonException as e -> Error [ $"not valid JSON: {e.Message}" ]
        else
            let lines = text.Replace("\r", "").Split('\n') |> Array.indexed |> Array.filter (fun (_, l) -> l.Trim() <> "")
            match Array.toList lines with
            | [] -> Error [ "empty file" ]
            | (_, header) :: rows ->
                let cols = header.Split(',') |> Array.map (fun c -> c.Trim())
                [ for n, l in rows ->
                      let cells = l.Split(',') |> Array.map (fun c -> c.Trim())
                      n + 1, (if cells.Length = cols.Length then Array.zip cols cells |> Map.ofArray else Map.empty) ]
                |> Ok

    let private field (m: Map<string, string>) name = Map.tryFind name m |> Option.filter (fun v -> v <> "")

    let private parseAll (required: string list) (row: Map<string, string> -> Result<'a, string>) (text: string) : Result<'a list, string list> =
        match records text with
        | Error e -> Error e
        | Ok rows ->
            let results =
                rows
                |> List.map (fun (n, m) ->
                    match required |> List.filter (fun c -> (field m c).IsNone) with
                    | [] -> row m |> Result.mapError (fun e -> $"line {n}: {e}")
                    | missing ->
                        let names = String.concat ", " missing
                        Error $"line {n}: missing {names}")
            match results |> List.choose (function Error e -> Some e | _ -> None) with
            | [] -> results |> List.choose (function Ok r -> Some r | _ -> None) |> Ok
            | errors -> Error errors

    let private dec name (v: string) =
        match Decimal.TryParse(v, NumberStyles.Float, inv) with
        | true, d when d >= 0m -> Ok d
        | _ -> Error(name + " must be a non-negative number, got '" + v + "'")

    let private lng name (v: string) =
        match Int64.TryParse(v, NumberStyles.Integer, inv) with
        | true, d when d >= 0L -> Ok d
        | _ -> Error(name + " must be a non-negative integer, got '" + v + "'")

    let private date name (v: string) =
        match DateOnly.TryParseExact(v, "yyyy-MM-dd", inv, DateTimeStyles.None) with
        | true, d -> Ok d
        | _ -> Error(name + " must be yyyy-MM-dd, got '" + v + "'")

    let private (>>=) r f = Result.bind f r

    /// Columns: model, in_per_mtok, out_per_mtok, currency, source, effective (yyyy-MM-dd).
    let parsePrices (text: string) : Result<PriceRow list, string list> =
        parseAll [ "model"; "in_per_mtok"; "out_per_mtok"; "currency"; "source"; "effective" ]
            (fun m ->
                dec "in_per_mtok" m["in_per_mtok"] >>= fun i ->
                dec "out_per_mtok" m["out_per_mtok"] >>= fun o ->
                date "effective" m["effective"] >>= fun e ->
                Ok { Model = m["model"]; InPerMTok = i; OutPerMTok = o; Currency = m["currency"]; Source = m["source"]; Effective = e })
            text

    /// Columns: date (yyyy-MM-dd), model, tokens_in, tokens_out, cost.
    let parseSpend (text: string) : Result<SpendRow list, string list> =
        parseAll [ "date"; "model"; "tokens_in"; "tokens_out"; "cost" ]
            (fun m ->
                date "date" m["date"] >>= fun d ->
                lng "tokens_in" m["tokens_in"] >>= fun i ->
                lng "tokens_out" m["tokens_out"] >>= fun o ->
                dec "cost" m["cost"] >>= fun c ->
                Ok { Date = d; Model = m["model"]; TokensIn = i; TokensOut = o; Cost = c })
            text

    /// The latest price of the model effective on or before the date; None when there is none (the placeholder stays).
    let priceAt (rows: PriceRow list) (model: string) (on: DateOnly) : PriceRow option =
        rows |> List.filter (fun p -> p.Model = model && p.Effective <= on) |> List.sortBy (fun p -> p.Effective) |> List.tryLast

    let cost (p: PriceRow) (tokensIn: int64) (tokensOut: int64) : decimal =
        (decimal tokensIn * p.InPerMTok + decimal tokensOut * p.OutPerMTok) / 1000000m

[<RequireQualifiedAccess>]
module SpendBudget =
    let private rank =
        function
        | BudgetState.Ok -> 0
        | BudgetState.Soft -> 1
        | BudgetState.Downgrade -> 2
        | BudgetState.Blocked -> 3

    /// The state of spend against a limit: Soft from softAt, Downgrade from downgradeAt, Blocked only beyond the limit
    /// (as `ItemBudget`: spending exactly the limit still may start). A zero limit is Blocked once anything is spent.
    let stateOf (limits: BudgetLimits) (spent: decimal) (limit: decimal) : BudgetState =
        if spent > limit then BudgetState.Blocked
        elif limit = 0m then BudgetState.Ok
        else
            let ratio = float (spent / limit)
            if ratio >= limits.DowngradeAt then BudgetState.Downgrade
            elif ratio >= limits.SoftAt then BudgetState.Soft
            else BudgetState.Ok

    /// The worse of two states: the item budget and the user budget both bind.
    let combine (a: BudgetState) (b: BudgetState) : BudgetState = if rank a >= rank b then a else b

    /// The user's monthly state from the latest import: (importedAt, (spent, cap)). A missing or older-than-`maxAgeDays`
    /// import is unknown, never silently Ok: it is Soft, with a warning naming the age (still Blocked if the stale
    /// figure is already over the cap).
    let userState
        (limits: BudgetLimits)
        (maxAgeDays: int)
        (now: DateTimeOffset)
        (importedAt: DateTimeOffset option)
        (spend: (decimal * decimal) option)
        : BudgetState * string option =
        match importedAt, spend with
        | Some at, Some(spent, cap) ->
            let age = int (Math.Floor((now - at).TotalDays))
            let state = stateOf limits spent cap
            if age > maxAgeDays then
                combine state BudgetState.Soft, Some $"the user spend import is {age} days old (limit {maxAgeDays}): treating the user budget as soft"
            else state, None
        | _ -> BudgetState.Soft, Some "no user spend import: treating the user budget as soft"

    /// What designc may be told: its `item_budget` row only says within or exceeded.
    let projected (s: BudgetState) : string = if s = BudgetState.Blocked then "exceeded" else "within"
