module Factory.Core.Tests.SuiteTests

open Factory.Core
open Factory.Core.Tests.Support

let private testProject = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" /></ItemGroup></Project>"""
let private library = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"""

let tests: (string * (unit -> unit)) list =
    [ "suite: one solution or project at the root is today's `dotnet test`", fun () ->
          equal (SuitePlan.Command "dotnet test") (Suite.discover [ "App.sln" ] [ "src/A/A.csproj", library; "tests/A.Tests/A.Tests.csproj", testProject ] []) "solution"
          equal (SuitePlan.Command "dotnet test") (Suite.discover [] [ "App.fsproj", library ] []) "project"
      "suite: no single root entry runs every test project, all of them even when one fails", fun () ->
          let projects =
              [ "b/tests/B.Tests/B.Tests.csproj", testProject
                "a/src/A/A.csproj", library
                "a/tests/A.Tests/A.Tests.fsproj", """<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""" ]
          equal (SuitePlan.Command "s=0; for p in 'a/tests/A.Tests/A.Tests.fsproj' 'b/tests/B.Tests/B.Tests.csproj'; do dotnet test \"$p\" || s=1; done; exit $s")
              (Suite.discover [] projects []) "per project, in path order"
          equal (SuitePlan.Command "s=0; for p in 'T.Tests/T.Tests.csproj'; do dotnet test \"$p\" || s=1; done; exit $s")
              (Suite.discover [ "One.sln"; "Two.sln" ] [ "T.Tests/T.Tests.csproj", testProject ] []) "two root solutions are not one"
      "suite: build output, packages and the factory's own state are not the repo's projects", fun () ->
          let copies = [ "x/bin/Debug/T.Tests.csproj"; "x/obj/T.Tests.csproj"; "node_modules/p/T.csproj"; ".factory/worktrees/t/T.Tests.csproj"; ".git/T.csproj" ]
          equal SuitePlan.NoSuite (Suite.discover [] (copies |> List.map (fun p -> p, testProject)) []) "all ignored"
      "suite: a new repo with no test project yet has no suite", fun () ->
          equal SuitePlan.NoSuite (Suite.discover [] [] []) "empty"
          equal SuitePlan.NoSuite (Suite.discover [] [ "src/A/A.csproj", library ] []) "libraries only"
      "suite: a quote in a path cannot end the shell word", fun () ->
          equal (SuitePlan.Command "s=0; for p in 'it'\\''s/T.Tests.csproj'; do dotnet test \"$p\" || s=1; done; exit $s")
              (Suite.discover [] [ "it's/T.Tests.csproj", testProject ] []) "quoted" 
      "suite: a package with a real test script runs `npm test` in its folder, next to the .NET projects", fun () ->
          let web = "web/package.json", """{ "scripts": { "test": "vitest run" } }"""
          let placeholder = "tools/x/package.json", """{ "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }"""
          let noScript = "docs/package.json", """{ "name": "docs" }"""
          equal (SuitePlan.Command "s=0; (cd 'web' && npm test) || s=1; exit $s") (Suite.discover [] [] [ web; placeholder; noScript ]) "npm only"
          equal (SuitePlan.Command "s=0; for p in 'T.Tests/T.Tests.csproj'; do dotnet test \"$p\" || s=1; done; (cd 'web' && npm test) || s=1; exit $s")
              (Suite.discover [] [ "T.Tests/T.Tests.csproj", testProject ] [ web ]) "both"
          equal (SuitePlan.Command "s=0; dotnet test || s=1; (cd 'web' && npm test) || s=1; exit $s")
              (Suite.discover [ "App.sln" ] [] [ web ]) "root solution and a package"
      "suite: a root package with a test script runs alone; packages' own copies are not the repo's", fun () ->
          let root = "package.json", """{ "workspaces": [ "packages/*" ], "scripts": { "test": "npm test --workspaces" } }"""
          let child = "packages/a/package.json", """{ "scripts": { "test": "jest" } }"""
          let installed = "node_modules/jest/package.json", """{ "scripts": { "test": "jest" } }"""
          equal (SuitePlan.Command "s=0; npm test || s=1; exit $s") (Suite.discover [] [] [ root; child; installed ]) "root only"
          equal (SuitePlan.Command "s=0; (cd 'packages/a' && npm test) || s=1; exit $s") (Suite.discover [] [] [ child; installed ]) "installed copies ignored"
      "suite: a package.json that is not JSON has no test script", fun () ->
          equal SuitePlan.NoSuite (Suite.discover [] [] [ "web/package.json", "{ not json" ]) "unreadable" ]
