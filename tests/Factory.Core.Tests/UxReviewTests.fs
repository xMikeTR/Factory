module Factory.Core.Tests.UxReviewTests

open Factory.Core
open Factory.Core.Tests.Support

let tests: (string * (unit -> unit)) list =
    [ "ux: the slug matches designc's element slug", fun () ->
          equal "quotes-quote-ui" (UxReview.slug "Quotes/Quote UI") "slug"
      "ux: a screenshot name is element, state and breakpoint; anything else is not a shot", fun () ->
          equal (Some("quotes-quote-ui", "empty", "mobile")) (UxReview.parseShot "out/quotes-quote-ui__empty__mobile.png" |> Option.map (fun s -> s.ElementSlug, s.State, s.Breakpoint)) "shot"
          equal None (UxReview.parseShot "out/readme.png") "no parts"
          equal None (UxReview.parseShot "out/a__b__c.jpg") "not png"
      "ux: scores outside 1 to 5 are not recorded", fun () ->
          check (UxReview.parseScore 6 3 3 "x" |> Result.isError) "6"
          check (UxReview.parseScore 3 0 3 "x" |> Result.isError) "0"
          equal (Ok 4.0) (UxReview.parseScore 5 4 3 "none" |> Result.map UxReview.mean) "mean"
      "ux: element scores average over every state and breakpoint", fun () ->
          let shot st = { ElementSlug = "a"; State = st; Breakpoint = "mobile"; Path = "" }
          let score h = { Hierarchy = h; Clarity = h; Consistency = h; Note = "" }
          equal [ "a", 3.0 ] (UxReview.perElement [ shot "ready", score 2; shot "empty", score 4 ]) "avg" ]
