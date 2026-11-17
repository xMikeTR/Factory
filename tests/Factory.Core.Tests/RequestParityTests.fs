module Factory.Core.Tests.RequestParityTests

open Factory.Core
open Factory.Core.Tests.Support

/// Expected values come from designc's renderer/parity.js (Parity.shape, Parity.sameShape), run on these inputs when the table was made.
let private shapes =
    [ // text, shape
      "", "empty"
      "  ", "empty"
      "15/01/2026", "date:dd/0d/dddd"
      "2026-01-15", "date:dddd-0d-dd"
      "1,000.00", "num:#,##0.00"
      "1000.00", "num:0.00"
      "500.00", "num:?0.00"
      "1,2", "digits:d,d"
      "1,200", "num:#,##0"
      "(500.00)", "num:(?0.00)"
      "-500.00", "num:-?0.00"
      "£1,200.00", "num:\u00a3#,##0.00"
      "12.5%", "num:?0.0%"
      "Bound", "text:Aa"
      "BOUND", "text:AA"
      "Mock Program Alpha", "text:Aa"
      "another one", "text:aa"
      "09:30", "time:0d:dd"
      "9:30", "time:d:dd"
      "2026-Q3", "text:A?"
      "A", "text:A?"
      "7", "num:?0"
      "$2,140,000", "num:$#,##0"
      "9531350", "num:0"
      "2026-10-04T10:00:00Z", "text:AA"
      "2026-10-04 10:00:00", "date:dddd-dd-0d dd:0d:0d"
      "1/2/26", "date:d/d/dd"
      "31.12.2025", "date:dd.dd.dddd"
      "USD", "text:AA"
      "usd", "text:aa"
      "McDonald", "text:aA"
      "Atlantic Mutual Re", "text:Aa"
      "7 lines missing location", "text:aa"
      "B-2", "num:B-?0"
      "ref 12345", "num:ref 0"
      "abcdefghij 12", "text:aa"
      "(12)", "num:(?0)"
      "12)", "digits:dd)"
      "EUR 1,000", "num:EUR #,##0"
      "1,000 EUR", "num:#,##0EUR"
      "x y", "text:aa"
      "  Trim me  ", "text:Aa"
      "O'Neil", "text:aA"
      "3.14", "num:?0.00"
      "1e5", "text:A?"
      "-0.5", "num:-?0.0"
      "2026-1-5", "date:dddd-d-d"
      "12:30:45", "time:dd:dd:dd"
      "012", "num:?0"
      "0123", "num:0"
      "Ünïcode Text", "text:aA"
      "TV 4K", "num:TV ?0K" ]

/// Pairs of shapes and Parity.sameShape's answer (every 1088 th disagreement and every 6th agreement of all unique-shape pairs).
let private same =
    [ // a, b, same
      "date:dd/0d/dddd", "date:dddd-0d-dd", false
      "date:dd/0d/dddd", "text:aa", false
      "date:dd/0d/dddd", "num:ref 0", false
      "date:dddd-0d-dd", "num:?0.00", false
      "date:dddd-0d-dd", "text:A?", false
      "date:dddd-0d-dd", "num:EUR #,##0", false
      "num:#,##0.00", "num:?0.0%", false
      "num:#,##0.00", "date:dd.dd.dddd", false
      "num:0.00", "date:dd/0d/dddd", false
      "num:0.00", "text:A?", false
      "num:0.00", "num:EUR #,##0", false
      "num:?0.00", "text:Aa", false
      "num:?0.00", "text:aA", false
      "digits:d,d", "date:dddd-0d-dd", false
      "digits:d,d", "time:0d:dd", false
      "digits:d,d", "num:(?0)", false
      "num:#,##0", "num:?0.00", false
      "num:#,##0", "num:$#,##0", false
      "num:#,##0", "date:dddd-d-d", false
      "num:(?0.00)", "text:aa", false
      "num:(?0.00)", "num:ref 0", false
      "num:-?0.00", "num:#,##0", false
      "num:-?0.00", "num:0", false
      "num:-?0.00", "date:dddd-d-d", false
      "num:\u00a3#,##0.00", "num:?0.0%", false
      "num:\u00a3#,##0.00", "date:dd.dd.dddd", false
      "num:?0.0%", "date:dd/0d/dddd", false
      "num:?0.0%", "text:aa", false
      "num:?0.0%", "num:ref 0", false
      "text:Aa", "num:0.00", false
      "text:Aa", "num:?0", false
      "text:Aa", "num:#,##0EUR", false
      "text:AA", "num:(?0.00)", false
      "text:AA", "date:d/d/dd", false
      "text:AA", "num:TV ?0K", false
      "text:aa", "text:Aa", false
      "text:aa", "num:ref 0", false
      "time:0d:dd", "num:0.00", false
      "time:0d:dd", "text:A?", false
      "time:0d:dd", "num:EUR #,##0", false
      "time:d:dd", "num:#,##0", false
      "time:d:dd", "num:0", false
      "time:d:dd", "date:dddd-d-d", false
      "text:A?", "num:\u00a3#,##0.00", false
      "text:A?", "num:(?0)", false
      "num:?0", "num:?0.00", false
      "num:?0", "num:$#,##0", false
      "num:?0", "time:dd:dd:dd", false
      "num:$#,##0", "num:?0.0%", false
      "num:$#,##0", "text:aA", false
      "num:0", "date:dddd-0d-dd", false
      "num:0", "text:aa", false
      "num:0", "num:EUR #,##0", false
      "date:dddd-dd-0d dd:0d:0d", "num:#,##0", false
      "date:dddd-dd-0d dd:0d:0d", "num:$#,##0", false
      "date:dddd-dd-0d dd:0d:0d", "date:dddd-d-d", false
      "date:d/d/dd", "num:\u00a3#,##0.00", false
      "date:d/d/dd", "date:dd.dd.dddd", false
      "date:dd.dd.dddd", "date:dd/0d/dddd", false
      "date:dd.dd.dddd", "text:AA", false
      "date:dd.dd.dddd", "num:ref 0", false
      "text:aA", "num:0.00", false
      "text:aA", "time:d:dd", false
      "text:aA", "num:#,##0EUR", false
      "num:B-?0", "num:(?0.00)", false
      "num:B-?0", "num:0", false
      "num:B-?0", "time:dd:dd:dd", false
      "num:ref 0", "num:?0.0%", false
      "num:ref 0", "date:dd.dd.dddd", false
      "num:(?0)", "date:dddd-0d-dd", false
      "num:(?0)", "time:0d:dd", false
      "num:(?0)", "num:#,##0EUR", false
      "digits:dd)", "num:(?0.00)", false
      "digits:dd)", "num:0", false
      "digits:dd)", "time:dd:dd:dd", false
      "num:EUR #,##0", "num:?0.0%", false
      "num:EUR #,##0", "date:dd.dd.dddd", false
      "num:#,##0EUR", "date:dddd-0d-dd", false
      "num:#,##0EUR", "text:aa", false
      "num:#,##0EUR", "num:ref 0", false
      "num:-?0.0", "num:?0.00", false
      "num:-?0.0", "text:A?", false
      "num:-?0.0", "num:EUR #,##0", false
      "date:dddd-d-d", "num:(?0.00)", false
      "date:dddd-d-d", "num:0", false
      "date:dddd-d-d", "time:dd:dd:dd", false
      "time:dd:dd:dd", "num:?0.0%", false
      "time:dd:dd:dd", "date:dd.dd.dddd", false
      "num:TV ?0K", "date:dddd-0d-dd", false
      "num:TV ?0K", "text:aa", false
      "num:TV ?0K", "num:ref 0", false
      "empty", "date:dd/0d/dddd", true
      "empty", "num:#,##0", true
      "empty", "text:AA", true
      "empty", "num:$#,##0", true
      "empty", "num:B-?0", true
      "empty", "num:-?0.0", true
      "num:#,##0.00", "empty", true
      "num:0.00", "num:(?0.00)", true
      "num:?0.00", "num:-?0.00", true
      "num:(?0.00)", "num:#,##0.00", true
      "num:-?0.00", "num:?0.00", true
      "text:AA", "text:A?", true
      "text:A?", "text:Aa", true
      "num:?0", "num:0", true
      "date:dddd-dd-0d dd:0d:0d", "empty", true
      "num:ref 0", "empty", true
      "num:EUR #,##0", "empty", true ]

let private log m p b : LoggedRequest = { Method = m; Path = p; Matched = true; Body = b }

let tests : (string * (unit -> unit)) list =
    [ "shape: the port matches designc's Parity.shape on a table of values (2026-Q3 is text:A?)", fun () ->
          for text, want in shapes do
              equal want (Shape.shape text) ("shape of " + text)
      "sameShape: the port matches designc's Parity.sameShape", fun () ->
          for a, b, want in same do
              equal want (Shape.sameShape a b) (a + " ~ " + b)
      "compare: an endpoint outside the spec fails and is named with its method", fun () ->
          let r = RequestParity.compare [ log "GET" "/api/bordereaux" None; log "GET" "/api/unlisted" None ] [ "GET /api/bordereaux" ] None
          equal [ "unlisted request: GET /api/unlisted is not in the spec's data sources" ] r.Failures "failures"
      "compare: a listed endpoint no state called fails; :param matches any segment and numeric ids template to :id", fun () ->
          let r = RequestParity.compare [ log "GET" "/api/bordereaux/B-2" None; log "GET" "/api/bordereaux/17" None ] [ "GET /api/bordereaux/:id"; "GET /api/bordereaux/summary" ] None
          equal [ "never called: GET /api/bordereaux/summary is in the spec's data sources but no state requested it" ] r.Failures "failures"
      "compare: a request body shape that differs from legacy fails; the same shape passes; no baseline compares nothing", fun () ->
          let mine = Some(Obj(Map [ "name", Leaf "text:Aa" ]))
          let theirs = Some(Obj(Map [ "name", Leaf "text:Aa"; "n", Leaf "num:0" ]))
          let leg b = Some [ { Method = "POST"; Path = "/api/x"; Request = b } ]
          check ((RequestParity.compare [ log "POST" "/api/x" mine ] [ "POST /api/x" ] (leg mine)).Failures.IsEmpty) "same"
          check ((RequestParity.compare [ log "POST" "/api/x" mine ] [ "POST /api/x" ] (leg theirs)).Failures.Length = 1) "differs"
          check ((RequestParity.compare [ log "POST" "/api/x" mine ] [ "POST /api/x" ] None).Failures.IsEmpty) "no baseline"
      "compare: a legacy endpoint the spec drops is a note, not a failure", fun () ->
          let r = RequestParity.compare [ log "GET" "/api/a" None ] [ "GET /api/a" ] (Some [ { Method = "GET"; Path = "/api/old"; Request = None } ])
          check r.Failures.IsEmpty "no failure"
          equal [ "dropped: legacy called GET /api/old and the spec does not list it" ] r.Notes "notes"
    ]
