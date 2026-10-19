module Factory.Core.Tests.ContextQualityTests

open System
open Factory.Core
open Factory.Core.Tests.Support

let private loneSurrogate (s: string) =
    s
    |> Seq.mapi (fun i c ->
        (Char.IsHighSurrogate c && (i + 1 >= s.Length || not (Char.IsLowSurrogate s.[i + 1])))
        || (Char.IsLowSurrogate c && (i = 0 || not (Char.IsHighSurrogate s.[i - 1]))))
    |> Seq.exists id

let tests: (string * (unit -> unit)) list =
    [
      "context: a file cut to the budget never ends in half a surrogate pair", fun () ->
          let entry: ContextEntry = { Path = "a.cs"; Source = Default }
          let text = String.replicate 200 "😀"
          for budget in 100 .. 112 do
              let packed = ContextPack.fit { MaxNominations = 1; MaxChars = budget } [ entry, text ]
              for f in packed do
                  check f.Truncated "cut"
                  check (not (loneSurrogate f.Text)) $"budget {budget}: broken pair"
                  check (f.Text.Length <= budget) $"budget {budget}: over budget"

      "review diff: a path git quotes (non-ASCII, quote or backslash) is read as the path a finding cites", fun () ->
          let diff =
              String.concat "\n"
                  [ "diff --git \"a/src/caf\\303\\251.cs\" \"b/src/caf\\303\\251.cs\""
                    "--- \"a/src/caf\\303\\251.cs\""
                    "+++ \"b/src/caf\\303\\251.cs\""
                    "@@ -1,1 +1,2 @@"
                    " keep"
                    "+new"
                    "+++ \"b/src/say \\\"hi\\\".cs\""
                    "@@ -1,1 +1,2 @@"
                    " keep"
                    "+new" ]
          let d = Diff.parse diff
          equal (Set.ofList [ 2 ]) d.Added.["src/café.cs"] "octal-escaped UTF-8"
          // the second '+++' line sits inside the first hunk's counts only if the counts were wrong; here the hunk ended
          equal (Set.ofList [ 2 ]) d.Added.["src/say \"hi\".cs"] "escaped quote"

      "paths: a segment with many stars does not take exponential time", fun () ->
          let pat = PathPattern.createOrFail (String.replicate 14 "*a" + "*b")
          let sw = Diagnostics.Stopwatch.StartNew()
          check (not (PathPattern.matches pat (String.replicate 60 "a"))) "no b"
          check (PathPattern.matches pat (String.replicate 60 "a" + "b")) "ends in b"
          check (sw.ElapsedMilliseconds < 1000L) $"took {sw.ElapsedMilliseconds} ms"
      "paths: repeated ** segments do not take exponential time", fun () ->
          let pat = PathPattern.createOrFail (String.replicate 12 "**/" + "x.cs")
          let path = String.replicate 30 "d/" + "y.cs"
          let sw = Diagnostics.Stopwatch.StartNew()
          check (not (PathPattern.matches pat path)) "no x.cs"
          check (PathPattern.matches pat (String.replicate 30 "d/" + "x.cs")) "x.cs"
          check (sw.ElapsedMilliseconds < 1000L) $"took {sw.ElapsedMilliseconds} ms"

      "quality: overloads of one name in one file are judged each against its own baseline", fun () ->
          let t = { MaxCyclomatic = 10; MaxCrap = 30.0; MaxLines = 60 }
          let f line c = { File = "a.cs"; Name = "Parse"; Line = line; Cyclomatic = c; Lines = 10; Coverage = Some 1.0 }
          let both = [ { File = "a.cs"; First = 1; Last = 100 } ]
          // The 15-complexity overload is old debt and unchanged; the other is small. Neither is a violation.
          equal [] (Quality.check t [ f 1 15; f 30 2 ] [ f 1 15; f 30 2 ] both) "unchanged overloads"
          // The small overload growing is held to its own baseline (2), not the other overload's (15).
          equal [ Some 2.0 ] (Quality.check t [ f 1 15; f 30 2 ] [ f 1 15; f 30 4 ] both |> List.filter (fun v -> v.Measure = Cyclomatic) |> List.map (fun v -> v.Baseline)) "small overload grew"
          // The old one getting worse is still caught.
          equal [ Cyclomatic; Crap ] (Quality.check t [ f 1 15; f 30 2 ] [ f 1 16; f 30 2 ] both |> List.map (fun v -> v.Measure)) "debt grew"
    ]
