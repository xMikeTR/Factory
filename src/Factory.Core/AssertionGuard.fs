namespace Factory.Core

open System.Text.RegularExpressions

/// A test file that asserts less than it did on the integration branch.
type AssertionDrop = { File: string; Before: int; After: int }

/// The deterministic half of "no weakened tests": per test file, the number of assertion calls may not go down.
/// It counts, it does not understand: an assertion swapped for a weaker one keeps the count and is left to the reviewer.
[<RequireQualifiedAccess>]
module AssertionGuard =

    let private codeExtensions = set [ ".cs"; ".fs"; ".ts"; ".tsx"; ".js"; ".jsx"; ".mjs"; ".py" ]
    let private testFolders = set [ "test"; "tests"; "__tests__"; "e2e"; "spec" ]
    let private testName = Regex(@"(\.(test|spec)\.[cm]?[jt]sx?$)|(Tests?\.(cs|fs)$)|(^test_.*\.py$)", RegexOptions.Compiled)

    let isTestFile (path: string) =
        let parts = path.Replace('\\', '/').Split('/')
        let name = parts[parts.Length - 1]
        codeExtensions.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant())
        && (testName.IsMatch name || parts[.. parts.Length - 2] |> Array.exists (fun d -> testFolders.Contains(d.ToLowerInvariant())))

    // xUnit/NUnit/MSTest, FluentAssertions, Expecto, Unquote, Jest/Vitest/Playwright expect, node/python assert.
    let private assertion =
        Regex(@"\bAssert\.\w+|\.Should\(\)|\bExpect\.\w+|\btest\s*<@|=!|\bexpect(\.\w+)*\s*\(|\bassert(\.\w+)*\s*\(|^\s*assert\s", RegexOptions.Compiled ||| RegexOptions.Multiline)

    /// Assertion calls in a source, full-line `//` and `#` comments left out.
    let count (source: string) =
        source.Split('\n')
        |> Array.filter (fun l -> let t = l.TrimStart() in not (t.StartsWith "//" || t.StartsWith "#"))
        |> Array.sumBy (fun l -> assertion.Matches(l).Count)

    /// (path, on the integration branch, now) for each path the task touched; None = absent. New test files are never a drop.
    let check (files: (string * string option * string option) list) : AssertionDrop list =
        [ for path, before, after in files do
              match before with
              | Some b when isTestFile path ->
                  let was, now = count b, after |> Option.map count |> Option.defaultValue 0
                  if now < was then { File = path; Before = was; After = now }
              | _ -> () ]
