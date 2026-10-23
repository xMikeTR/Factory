module Factory.Core.Tests.AssertionGuardTests

open Factory.Core
open Factory.Core.Tests.Support

let tests: (string * (unit -> unit)) list =
    [ "assertion guard: test files are found by folder or name, source files are not", fun () ->
          equal true (AssertionGuard.isTestFile "tests/Calc.Tests/CalcTests.cs") "tests folder"
          equal true (AssertionGuard.isTestFile "src/CalcTest.fs") "Test suffix"
          equal true (AssertionGuard.isTestFile "web/src/calc.test.ts") ".test."
          equal true (AssertionGuard.isTestFile "app/e2e/acceptance/W-1.spec.tsx") ".spec."
          equal true (AssertionGuard.isTestFile "web/src/__tests__/calc.jsx") "__tests__"
          equal false (AssertionGuard.isTestFile "src/Calc.cs") "source"
          equal false (AssertionGuard.isTestFile "tests/fixtures/data.json") "not code"
          equal false (AssertionGuard.isTestFile "src/Testing.cs") "name only contains test"
      "assertion guard: counts xUnit, FluentAssertions, Expecto, Unquote and expect/assert calls", fun () ->
          let src =
              "Assert.Equal(1, x);\nx.Should().Be(1);\nExpect.equal a b \"m\"\ntest <@ a = b @>\na =! b\n"
              + "expect(x).toBe(1);\nawait expect.soft(page).toHaveURL('/');\nassert.equal(a, b);\nassert(x)\n"
          equal 9 (AssertionGuard.count src) "one per call"
      "assertion guard: commented-out assertions do not count", fun () ->
          equal 1 (AssertionGuard.count "expect(a).toBe(1);\n  // expect(b).toBe(2);\n/* x */") "line comment"
      "assertion guard: fewer assertions in a test file than on the integration branch is a drop", fun () ->
          let before = "Assert.True(a);\nAssert.True(b);\n"
          equal [ { File = "tests/T.cs"; Before = 2; After = 1 } ]
              (AssertionGuard.check [ "tests/T.cs", Some before, Some "Assert.True(a);\n" ]) "drop"
      "assertion guard: a deleted test file drops to zero", fun () ->
          equal [ { File = "tests/T.cs"; Before = 1; After = 0 } ] (AssertionGuard.check [ "tests/T.cs", Some "Assert.True(a);", None ]) "deleted"
      "assertion guard: new files, equal and more assertions are clean", fun () ->
          equal [] (AssertionGuard.check [ "tests/New.cs", None, Some "Assert.True(a);" ]) "new"
          equal [] (AssertionGuard.check [ "tests/T.cs", Some "Assert.True(a);", Some "Assert.False(b);" ]) "same count"
          equal [] (AssertionGuard.check [ "tests/T.cs", Some "Assert.True(a);", Some "Assert.True(a);\nAssert.True(b);" ]) "more"
      "assertion guard: only test files are judged", fun () ->
          equal [] (AssertionGuard.check [ "src/Calc.cs", Some "Assert.True(a);", Some "" ]) "source file" ]
