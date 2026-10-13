module Factory.Core.Tests.ItemBudgetTests

open Factory.Core
open Factory.Core.Tests.Support

let private policy = { Default = Some 1000L; Overrides = Map [ "W-BIG", 5000L ] }

let tests: (string * (unit -> unit)) list =
    [ "item budget: an item's limit is its override, else the default, else none", fun () ->
          equal (Some 5000L) (ItemBudget.limitFor policy "W-BIG") "override"
          equal (Some 1000L) (ItemBudget.limitFor policy "W-1") "default"
          equal None (ItemBudget.limitFor { Default = None; Overrides = Map.empty } "W-1") "none"
      "item budget: spend up to the limit is within, beyond it is exceeded", fun () ->
          equal (Some { Item = "W-1"; Status = Within; Limit = 1000L; Spent = 1000L }) (ItemBudget.read policy "W-1" 1000L) "at limit"
          equal (Some { Item = "W-1"; Status = Exceeded; Limit = 1000L; Spent = 1001L }) (ItemBudget.read policy "W-1" 1001L) "over"
      "item budget: no limit configured means no reading", fun () ->
          equal None (ItemBudget.read { Default = None; Overrides = Map.empty } "W-1" 99999L) "unlimited" ]
