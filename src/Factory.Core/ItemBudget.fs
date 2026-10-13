namespace Factory.Core

/// What the pipeline allows one work item to spend, in tokens. Designc only displays the result (`item_budget`);
/// the limit and its enforcement are S2's.
type BudgetPolicy =
    { Default: int64 option
      Overrides: Map<string, int64> }

type BudgetStatus =
    | Within
    | Exceeded

type BudgetReading =
    { Item: string
      Status: BudgetStatus
      Limit: int64
      Spent: int64 }

[<RequireQualifiedAccess>]
module ItemBudget =
    let limitFor (policy: BudgetPolicy) (item: string) : int64 option =
        match Map.tryFind item policy.Overrides with
        | Some l -> Some l
        | None -> policy.Default

    /// None when the item has no limit: nothing to report.
    let read (policy: BudgetPolicy) (item: string) (spent: int64) : BudgetReading option =
        limitFor policy item
        |> Option.map (fun limit ->
            { Item = item; Status = (if spent > limit then Exceeded else Within); Limit = limit; Spent = spent })
