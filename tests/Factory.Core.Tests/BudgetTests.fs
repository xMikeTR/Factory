module Factory.Core.Tests.BudgetTests

open System
open Factory.Core
open Factory.Core.Tests.Support

let private pricesCsv =
    "model,in_per_mtok,out_per_mtok,currency,source,effective\n"
    + "small,0.10,0.40,GBP,invoice-oct,2026-10-01\n"
    + "std,3,15,GBP,invoice-oct,2026-10-01\n"

let private spendCsv =
    "date,model,tokens_in,tokens_out,cost\n"
    + "2026-11-01,small,1000,200,0.5\n"
    + "2026-11-02,std,2000,100,1.25\n"

let private limits = { SoftAt = 0.8; DowngradeAt = 0.9 }

let tests: (string * (unit -> unit)) list =
    [ "budget import: a prices CSV parses to effective-dated rows", fun () ->
          match BudgetImport.parsePrices pricesCsv with
          | Ok rows ->
              equal 2 rows.Length "rows"
              equal { Model = "std"; InPerMTok = 3m; OutPerMTok = 15m; Currency = "GBP"; Source = "invoice-oct"; Effective = DateOnly(2026, 10, 1) } rows.[1] "std"
          | Error e -> raise (AssertFailed $"{e}")
      "budget import: prices as JSON equal prices as CSV", fun () ->
          let json = """[{"model":"small","in_per_mtok":0.10,"out_per_mtok":0.40,"currency":"GBP","source":"invoice-oct","effective":"2026-10-01"},
                         {"model":"std","in_per_mtok":3,"out_per_mtok":15,"currency":"GBP","source":"invoice-oct","effective":"2026-10-01"}]"""
          equal (BudgetImport.parsePrices pricesCsv) (BudgetImport.parsePrices json) "same rows"
      "budget import: a bad row is rejected with its line, nothing is half-imported", fun () ->
          match BudgetImport.parsePrices (pricesCsv + "bad,x,1,GBP,s,2026-10-01\n") with
          | Error es -> check (es |> List.exists (fun e -> e.Contains "line 4")) $"names the line: {es}"
          | Ok _ -> raise (AssertFailed "accepted a non-numeric price")
      "budget import: a negative price or a missing column is rejected", fun () ->
          check (Result.isError (BudgetImport.parsePrices "model,in_per_mtok\nsmall,1\n")) "missing columns"
          check (Result.isError (BudgetImport.parsePrices (pricesCsv + "neg,-1,1,GBP,s,2026-10-01\n"))) "negative"
      "budget import: spend parses from CSV and JSON", fun () ->
          match BudgetImport.parseSpend spendCsv with
          | Ok rows ->
              equal 2 rows.Length "rows"
              equal { Date = DateOnly(2026, 11, 2); Model = "std"; TokensIn = 2000L; TokensOut = 100L; Cost = 1.25m } rows.[1] "row"
              let json = """[{"date":"2026-11-01","model":"small","tokens_in":1000,"tokens_out":200,"cost":0.5},{"date":"2026-11-02","model":"std","tokens_in":2000,"tokens_out":100,"cost":1.25}]"""
              equal (Ok rows) (BudgetImport.parseSpend json) "json"
          | Error e -> raise (AssertFailed $"{e}")
      "budget: the price in force is the latest effective on or before the date", fun () ->
          let p d i = { Model = "std"; InPerMTok = i; OutPerMTok = i; Currency = "GBP"; Source = "s"; Effective = d }
          let rows = [ p (DateOnly(2026, 10, 1)) 1m; p (DateOnly(2026, 11, 1)) 2m ]
          equal (Some 1m) (BudgetImport.priceAt rows "std" (DateOnly(2026, 10, 20)) |> Option.map (fun p -> p.InPerMTok)) "october"
          equal (Some 2m) (BudgetImport.priceAt rows "std" (DateOnly(2026, 11, 1)) |> Option.map (fun p -> p.InPerMTok)) "on the day"
          equal None (BudgetImport.priceAt rows "std" (DateOnly(2026, 9, 1))) "before any"
          equal None (BudgetImport.priceAt rows "other" (DateOnly(2026, 11, 1))) "unknown model"
      "budget: cost is tokens times the price per million", fun () ->
          let p = { Model = "std"; InPerMTok = 3m; OutPerMTok = 15m; Currency = "GBP"; Source = "s"; Effective = DateOnly(2026, 10, 1) }
          equal 0.021m (BudgetImport.cost p 2000L 1000L) "2000 in + 1000 out"   // 0.006 + 0.015
      "budget: ratio thresholds map to states, over the limit is blocked", fun () ->
          equal BudgetState.Ok (SpendBudget.stateOf limits 79m 100m) "79%"
          equal BudgetState.Soft (SpendBudget.stateOf limits 80m 100m) "80%"
          equal BudgetState.Downgrade (SpendBudget.stateOf limits 90m 100m) "90%"
          equal BudgetState.Downgrade (SpendBudget.stateOf limits 100m 100m) "at the limit"
          equal BudgetState.Blocked (SpendBudget.stateOf limits 101m 100m) "over"
      "budget: a zero limit is blocked once anything is spent, ok before", fun () ->
          equal BudgetState.Ok (SpendBudget.stateOf limits 0m 0m) "nothing"
          equal BudgetState.Blocked (SpendBudget.stateOf limits 1m 0m) "something"
      "budget: combine is the worst state", fun () ->
          equal BudgetState.Downgrade (SpendBudget.combine BudgetState.Soft BudgetState.Downgrade) "soft+downgrade"
          equal BudgetState.Blocked (SpendBudget.combine BudgetState.Blocked BudgetState.Ok) "blocked+ok"
          equal BudgetState.Ok (SpendBudget.combine BudgetState.Ok BudgetState.Ok) "ok+ok"
      "budget: a user import older than the maximum age is unknown, treated as soft with a warning", fun () ->
          let now = DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)
          let fresh, w1 = SpendBudget.userState limits 2 now (Some(now.AddDays -1.0)) (Some(100m, 1000m))
          equal BudgetState.Ok fresh "fresh"
          equal None w1 "no warning"
          let stale, w2 = SpendBudget.userState limits 2 now (Some(now.AddDays -3.0)) (Some(100m, 1000m))
          equal BudgetState.Soft stale "stale"
          check (w2.IsSome && w2.Value.Contains "3") $"names the age: {w2}"
          let never, w3 = SpendBudget.userState limits 2 now None None
          equal BudgetState.Soft never "never imported"
          check w3.IsSome "warns"
      "budget: a stale import that is already blocked stays blocked", fun () ->
          let now = DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)
          let s, _ = SpendBudget.userState limits 2 now (Some(now.AddDays -5.0)) (Some(2000m, 1000m))
          equal BudgetState.Blocked s "over the cap"
      "budget: the projected item_budget status is within unless blocked", fun () ->
          equal "within" (SpendBudget.projected BudgetState.Ok) "ok"
          equal "within" (SpendBudget.projected BudgetState.Soft) "soft"
          equal "within" (SpendBudget.projected BudgetState.Downgrade) "downgrade"
          equal "exceeded" (SpendBudget.projected BudgetState.Blocked) "blocked" ]
