module Factory.Core.Tests.ApiScenariosTests

open Factory.Core
open Factory.Core.Tests.Support

let private examples = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "fares-v3")
let private baseHandover =
    match Json.parseHandover (System.IO.File.ReadAllText(System.IO.Path.Combine(examples, "handover.json"))) with
    | Ok h -> h
    | Error es -> failwithf "%A" es

let private ok r = match r with Ok v -> v | Error es -> failwithf "%A" es

let private elem (id: string) (repo: string option) : ElementSpec =
    { baseHandover.Elements.Head with Id = ElementId id; Name = id; Repo = repo }

let private serves (element: string) (id: string) (m: string) (path: string) : ElementInterface =
    { Element = element; Role = "serves"; From = "Api"; To = "Ui"; Kind = "operation"; Id = id
      Method = Some m; Path = Some path; File = Some "contracts/openapi/orders.yaml"; Version = "v1" }


/// designc's scenarios/2 example (ORD-1-ts-01), with a hand-written request on each example.
let private scenario : ApiScenario =
    { Id = "ORD-1-ts-01"
      Path = "test-scenarios/ORD-1-ts-01.yaml"
      Sha256 = "0801fb767b1f662d9a80e2b2ab9e77cb1fe37333f1abcc1b360ea0f3a92e2f29"
      OperationId = "tagOrders"
      Method = "POST"
      OpPath = "/orders/tags"
      Examples =
        [ { Index = 1
            Title = "prefix match"
            Given = [ "orders/Order", [ """{"id":1,"ref":"A-100"}"""; """{"id":2,"ref":"B-200"}""" ] ]
            Then = [ "orders/Order", [ """{"id":1,"tag":"Priority"}"""; """{"id":2,"tag":null}""" ] ]
            Request = Some { Body = Some """{"prefix":"A","tag":"Priority"}"""; Path = [] } }
          { Index = 2
            Title = "whole \"reference\""
            Given = []
            Then = [ "orders/Order", [ """{"id":2,"tag":"Hold"}""" ] ]
            Request = Some { Body = None; Path = [ "id", "2" ] } } ] }

let private handover =
    { baseHandover with
        Elements = [ elem "Ui" (Some "Ui"); elem "Api" (Some "Api") ]
        Interfaces = [ serves "Api" "tagOrders" "post" "/orders/tags" ]
        ApiScenarios = [ scenario ] }

let private cmp expected stored = ApiScenarios.compare "orders/Order" expected stored

let tests: (string * (unit -> unit)) list =
    [ "api scenarios: a row with an id matches the stored row with that id on the listed fields only", fun () ->
          equal [] (cmp [ """{"id":1,"tag":"Priority"}""" ] [ """{"id":1,"ref":"A","tag":"Priority"}""" ]) "id match, unlisted ref ignored"
      "api scenarios: an extra stored row is ignored and a listed null must be null", fun () ->
          equal [] (cmp [ """{"id":2,"tag":null}""" ] [ """{"id":1,"tag":"x"}"""; """{"id":2,"tag":null}""" ]) "null matches"
          equal [ "orders/Order id=2: tag expected null, got \"Priority\"" ] (cmp [ """{"id":2,"tag":null}""" ] [ """{"id":2,"tag":"Priority"}""" ]) "null mismatch text"
      "api scenarios: a missing id row and a missing field are named", fun () ->
          equal [ "orders/Order id=9: no stored row" ] (cmp [ """{"id":9,"tag":"x"}""" ] [ """{"id":1,"tag":"x"}""" ]) "no row"
          equal [ "orders/Order id=1: tag expected \"x\", got <missing>" ] (cmp [ """{"id":1,"tag":"x"}""" ] [ """{"id":1}""" ]) "missing field"
      "api scenarios: a row without an id matches some stored row on every listed field", fun () ->
          equal [] (cmp [ """{"ref":"B-200"}""" ] [ """{"id":1,"ref":"A-100"}"""; """{"id":2,"ref":"B-200","tag":"t"}""" ]) "match"
          equal [ "orders/Order: no stored row matches {\"ref\":\"Z\"}" ] (cmp [ """{"ref":"Z"}""" ] [ """{"id":1,"ref":"A-100"}""" ]) "no match text"
      "api scenarios: compare is deep and reports every miss in order", fun () ->
          equal [] (cmp [ """{"id":1,"meta":{"a":[1,2]}}""" ] [ """{"id":1,"meta":{"a":[1,2]}}""" ]) "deep equal"
          equal
              [ "orders/Order id=1: tag expected \"a\", got \"b\""; "orders/Order id=2: no stored row" ]
              (cmp [ """{"id":1,"tag":"a"}"""; """{"id":2}""" ] [ """{"id":1,"tag":"b"}""" ])
              "two misses"
      "api scenarios: the message and the display name name the scenario and example", fun () ->
          equal "ORD-1-ts-01 example 1 (prefix match): orders/Order id=2: no stored row" (ApiScenarios.message "ORD-1-ts-01" 1 "prefix match" "orders/Order id=2: no stored row") "message"
          equal "ORD-1-ts-01 example 1: prefix match" (ApiScenarios.displayName "ORD-1-ts-01" 1 "prefix match") "display name"
          equal (Some("ORD-1-ts-01", 2)) (ApiScenarios.parseDisplayName "ORD-1-ts-01 example 2: whole reference") "parse"
          equal None (ApiScenarios.parseDisplayName "SomeOtherTest") "other"
      "api scenarios: failures are classified from the first line", fun () ->
          equal "call" (ApiScenarios.classify "ORD-1-ts-01 example 1 (a): call POST /orders/tags failed: 500") "call"
          equal "rows" (ApiScenarios.classify "ORD-1-ts-01 example 1 (a): orders/Order id=2: tag expected null, got \"P\"") "rows"
          equal "rows" (ApiScenarios.classify "ORD-1-ts-01 example 1 (a): orders/Order id=2: no stored row") "rows no row"
          equal "seed" (ApiScenarios.classify "adapter: implement IScenarioAdapter once in this test project (found 0)") "seed"
      "api scenarios: clientMethod is the last underscore segment, PascalCase, plus Async", fun () ->
          equal "TagOrdersAsync" (Codegen.clientMethod "tagOrders") "plain"
          equal "TagAsync" (Codegen.clientMethod "orders_tag") "segment"
      "api scenarios: the id becomes a class name", fun () ->
          equal "Ord1Ts01" (ApiScenarios.idPascal "ORD-1-ts-01") "pascal"
          equal "S1A" (ApiScenarios.idPascal "1-a") "leading digit"
      "api scenarios: the serving interface is found by operation id, method (any case) and path", fun () ->
          equal "Api" (ok (Result.mapError List.singleton (Codegen.apiServing handover scenario))).Element "element"
      "api scenarios: an unmatched operation is an error that names the scenario", fun () ->
          let h = { handover with Interfaces = [ serves "Api" "tagOrders" "GET" "/orders/tags" ] }
          match ApiScenarios.files h with
          | Error [ m ] -> check (m.Contains "ORD-1-ts-01" && m.Contains "need exactly one") m
          | other -> failwithf "%A" other
      "api scenarios: two different serving elements are an error, the same one listed twice is not", fun () ->
          let two = { handover with Interfaces = [ serves "Api" "tagOrders" "POST" "/orders/tags"; serves "Ui" "tagOrders" "POST" "/orders/tags" ] }
          check (Result.isError (ApiScenarios.files two)) "two elements"
          let dup = { handover with Interfaces = [ serves "Api" "tagOrders" "POST" "/orders/tags"; serves "Api" "tagOrders" "POST" "/orders/tags" ] }
          check (Result.isOk (ApiScenarios.files dup)) "duplicate"
      "api scenarios: files are the runtime and one class per scenario, in the serving repo, byte-stable", fun () ->
          let a = ok (ApiScenarios.files handover)
          equal a (ok (ApiScenarios.files handover)) "stable"
          equal
              [ "Api", "generated/contracts/api-tests/Orders/Ord1Ts01.ApiScenario.g.cs"; "Api", "generated/contracts/api-tests/ScenarioRuntime.g.cs" ]
              (a |> List.map (fun (r, p, _) -> r, p))
              "paths"
          let text = a |> List.map (fun (_, _, t) -> t)
          check (text |> List.forall (fun t -> t.EndsWith "\n" && not (t.Contains "\r"))) "lf and trailing newline"
      "api scenarios: the scenario class is golden", fun () ->
          let (_, _, text) = ok (ApiScenarios.files handover) |> List.find (fun (_, p, _) -> p.EndsWith "ApiScenario.g.cs")
          let expected =
              String.concat "\n"
                  [ "// <auto-generated> Factory X4 from test-scenarios/ORD-1-ts-01.yaml sha256 0801fb767b1f. Do not edit."
                    "#nullable enable"
                    "using System.Collections.Generic;"
                    "using System.Threading.Tasks;"
                    "using Xunit;"
                    ""
                    "namespace Contracts.ApiScenarios;"
                    ""
                    "public sealed class Ord1Ts01"
                    "{"
                    "    [Fact(DisplayName = \"ORD-1-ts-01 example 1: prefix match\")]"
                    "    public async Task Example1()"
                    "    {"
                    "        await using var a = ScenarioRuntime.Adapter();"
                    "        var http = await a.StartAsync();"
                    "        await a.SeedAsync(\"orders/Order\", ScenarioRuntime.Rows(\"{\\\"id\\\":1,\\\"ref\\\":\\\"A-100\\\"}\", \"{\\\"id\\\":2,\\\"ref\\\":\\\"B-200\\\"}\"));"
                    "        await ScenarioRuntime.CallAsync(\"ORD-1-ts-01\", 1, \"prefix match\", new global::Contracts.Orders.OrdersClient(http), \"TagOrdersAsync\", \"POST\", \"/orders/tags\", new Dictionary<string, string>(), \"{\\\"prefix\\\":\\\"A\\\",\\\"tag\\\":\\\"Priority\\\"}\");"
                    "        ScenarioRuntime.Assert(\"ORD-1-ts-01\", 1, \"prefix match\", \"orders/Order\", ScenarioRuntime.Rows(\"{\\\"id\\\":1,\\\"tag\\\":\\\"Priority\\\"}\", \"{\\\"id\\\":2,\\\"tag\\\":null}\"), await a.QueryAsync(\"orders/Order\"));"
                    "    }"
                    "    [Fact(DisplayName = \"ORD-1-ts-01 example 2: whole \\\"reference\\\"\")]"
                    "    public async Task Example2()"
                    "    {"
                    "        await using var a = ScenarioRuntime.Adapter();"
                    "        var http = await a.StartAsync();"
                    "        await ScenarioRuntime.CallAsync(\"ORD-1-ts-01\", 2, \"whole \\\"reference\\\"\", new global::Contracts.Orders.OrdersClient(http), \"TagOrdersAsync\", \"POST\", \"/orders/tags\", new Dictionary<string, string> { [\"id\"] = \"2\" }, null);"
                    "        ScenarioRuntime.Assert(\"ORD-1-ts-01\", 2, \"whole \\\"reference\\\"\", \"orders/Order\", ScenarioRuntime.Rows(\"{\\\"id\\\":2,\\\"tag\\\":\\\"Hold\\\"}\"), await a.QueryAsync(\"orders/Order\"));"
                    "    }"
                    "}"
                    "" ]
          equal expected text "class text"
      "api scenarios: the runtime is the same text for every repo and names the adapter contract", fun () ->
          let h = { handover with Elements = [ elem "Ui" (Some "Ui"); elem "Api" (Some "Api"); elem "Api2" (Some "Api2") ]
                                  Interfaces = [ serves "Api" "tagOrders" "POST" "/orders/tags" ]
                                  ApiScenarios = [ scenario ] }
          let rt = ok (ApiScenarios.files h) |> List.filter (fun (_, p, _) -> p = ApiScenarios.runtimeFile)
          equal 1 rt.Length "one repo, one runtime"
          let (_, _, t) = rt.Head
          check (t.Contains "public interface IScenarioAdapter : IAsyncDisposable") "interface"
          check (t.Contains "adapter: implement IScenarioAdapter once in this test project (found {found.Count})") "adapter message"
          equal ApiScenarios.runtimeText t "shared text"
      "api scenarios: examples without a request generate nothing", fun () ->
          let bare = { scenario with Examples = scenario.Examples |> List.map (fun e -> { e with Request = None }) }
          equal [] (ok (ApiScenarios.files { handover with ApiScenarios = [ bare ] })) "nothing"
      "api scenarios: targets adds Client to a served source that has a generated scenario, and leaves the commands alone", fun () ->
          let without = ok (Codegen.targets { handover with ApiScenarios = [] } None) |> List.exactlyOne
          equal [ Codegen.Server ] without.Sides "no scenario: server only"
          let t = ok (Codegen.targets handover None) |> List.exactlyOne
          equal [ Codegen.Server; Codegen.Client ] t.Sides "scenario: both"
          let cmds = Codegen.commands t
          check (cmds |> List.exists (fun c -> c.Contains "/ClassName:OrdersClient /GenerateClientInterfaces:true /GenerateDtoTypes:false /InjectHttpClient:true /UseBaseUrl:false")) "client flags (the Dtos file owns ApiException)"
          check (cmds |> List.forall (fun c -> not (c.Contains "OperationGenerationMode"))) "no OperationGenerationMode"
          let bare = { scenario with Examples = scenario.Examples |> List.map (fun e -> { e with Request = None }) }
          equal [ Codegen.Server ] (ok (Codegen.targets { handover with ApiScenarios = [ bare ] } None) |> List.exactlyOne).Sides "no request: unchanged" ]
