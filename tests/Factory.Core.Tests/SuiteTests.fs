module Factory.Core.Tests.SuiteTests

open Factory.Core
open Factory.Core.Tests.Support

let private testProject = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" /></ItemGroup></Project>"""
let private library = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"""

let tests: (string * (unit -> unit)) list =
    [ "suite: one solution or project at the root is today's `dotnet test`", fun () ->
          equal (SuitePlan.Command "dotnet test") (Suite.discover [ "App.sln" ] [ "src/A/A.csproj", library; "tests/A.Tests/A.Tests.csproj", testProject ]) "solution"
          equal (SuitePlan.Command "dotnet test") (Suite.discover [] [ "App.fsproj", library ]) "project"
      "suite: no single root entry runs every test project, all of them even when one fails", fun () ->
          let projects =
              [ "b/tests/B.Tests/B.Tests.csproj", testProject
                "a/src/A/A.csproj", library
                "a/tests/A.Tests/A.Tests.fsproj", """<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""" ]
          equal (SuitePlan.Command "s=0; for p in 'a/tests/A.Tests/A.Tests.fsproj' 'b/tests/B.Tests/B.Tests.csproj'; do dotnet test \"$p\" || s=1; done; exit $s")
              (Suite.discover [] projects) "per project, in path order"
          equal (SuitePlan.Command "s=0; for p in 'T.Tests/T.Tests.csproj'; do dotnet test \"$p\" || s=1; done; exit $s")
              (Suite.discover [ "One.sln"; "Two.sln" ] [ "T.Tests/T.Tests.csproj", testProject ]) "two root solutions are not one"
      "suite: build output, packages and the factory's own state are not the repo's projects", fun () ->
          let copies = [ "x/bin/Debug/T.Tests.csproj"; "x/obj/T.Tests.csproj"; "node_modules/p/T.csproj"; ".factory/worktrees/t/T.Tests.csproj"; ".git/T.csproj" ]
          equal SuitePlan.NoSuite (Suite.discover [] (copies |> List.map (fun p -> p, testProject))) "all ignored"
      "suite: a new repo with no test project yet has no suite", fun () ->
          equal SuitePlan.NoSuite (Suite.discover [] []) "empty"
          equal SuitePlan.NoSuite (Suite.discover [] [ "src/A/A.csproj", library ]) "libraries only"
      "suite: a quote in a path cannot end the shell word", fun () ->
          equal (SuitePlan.Command "s=0; for p in 'it'\\''s/T.Tests.csproj'; do dotnet test \"$p\" || s=1; done; exit $s")
              (Suite.discover [] [ "it's/T.Tests.csproj", testProject ]) "quoted" ]
