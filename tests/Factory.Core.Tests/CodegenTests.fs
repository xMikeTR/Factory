module Factory.Core.Tests.CodegenTests

open Factory.Core
open Factory.Core.Tests.Support

let private examples = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "fares-v3")
let private baseHandover =
    match Json.parseHandover (System.IO.File.ReadAllText(System.IO.Path.Combine(examples, "handover.json"))) with
    | Ok h -> h
    | Error es -> failwithf "%A" es

let private elem (id: string) (repo: string option) : ElementSpec =
    { baseHandover.Elements.Head with Id = ElementId id; Name = id; Repo = repo }

let private iface (element: string) (role: string) (id: string) (file: string) : ElementInterface =
    { Element = element
      Role = role
      From = "Ui"
      To = "Api"
      Kind = (if role = "serves" || role = "calls" then "operation" else "event")
      Id = id
      Method = Some "GET"
      Path = Some "/x"
      File = (if role = "serves" || role = "calls" then Some file else None)
      Version = "v1" }

let private withIfaces (elements: ElementSpec list) (ifaces: ElementInterface list) =
    { baseHandover with Elements = elements; Interfaces = ifaces }

let private split =
    withIfaces
        [ elem "Ui" (Some "Ui"); elem "Api" (Some "Api") ]
        [ iface "Ui" "calls" "listOrders" "contracts/openapi/shop.yaml"
          iface "Api" "serves" "listOrders" "contracts/openapi/shop.yaml" ]

let private ok r = match r with Ok v -> v | Error es -> failwithf "%A" es

let private op (element: string) (role: string) (id: string) (m: string option) (path: string option) (file: string) =
    { iface element role id file with Method = m; Path = path }

let private tsSplit =
    withIfaces
        [ elem "Ui" (Some "Ui"); elem "Api" (Some "Api") ]
        [ op "Ui" "calls" "getOrder" (Some "get") (Some "/orders/{id}") "contracts/openapi/shop.yaml"
          op "Ui" "calls" "createOrder" (Some "post") (Some "/orders") "contracts/openapi/shop.yaml"
          op "Ui" "calls" "getOrder" (Some "get") (Some "/orders/{id}") "contracts/openapi/shop.yaml"
          op "Ui" "calls" "noMethod" None (Some "/x") "contracts/openapi/shop.yaml"
          op "Api" "serves" "getOrder" (Some "get") (Some "/orders/{id}") "contracts/openapi/shop.yaml"
          op "Api" "serves" "createOrder" (Some "post") (Some "/orders") "contracts/openapi/shop.yaml" ]

let private tsOne =
    ok (Codegen.tsTargets tsSplit None (Set.ofList [ "Ui" ])) |> List.exactlyOne

let tests: (string * (unit -> unit)) list =
    [ "codegen ts: split run, the calling node repo gets schema and client, the serving repo no TS by default", fun () ->
          let t = tsOne
          equal "Ui" t.Repo "repo"
          equal Codegen.TypeScript t.Language "language"
          equal [ "generated/contracts/ts/Shop/Shop.schema.ts"; "generated/contracts/ts/Shop/Shop.client.ts" ] t.Files "files"
          equal [ Codegen.Client ] t.Sides "sides"
          equal "" t.Namespace "namespace"
      "codegen ts: operations are distinct, sorted, upper-cased, and one without a method is skipped", fun () ->
          let o id m p : Codegen.ClientOp = { Id = id; Method = m; Path = p }
          equal [ o "createOrder" "POST" "/orders"; o "getOrder" "GET" "/orders/{id}" ] tsOne.Operations "ops"
      "codegen ts: a calling repo that is not a node repo gets nothing", fun () ->
          equal [] (ok (Codegen.tsTargets tsSplit None Set.empty)) "none"
      "codegen ts: policy list Api with Api serving only gives a schema and no client", fun () ->
          let t = ok (Codegen.tsTargets tsSplit (Some [ "Api" ]) Set.empty) |> List.exactlyOne
          equal "Api" t.Repo "repo"
          equal [ "generated/contracts/ts/Shop/Shop.schema.ts" ] t.Files "schema only"
          equal [] t.Operations "no ops"
      "codegen ts: a single-repo run generates for the dot repo only when it is a node repo", fun () ->
          let h = withIfaces [ elem "Api" None; elem "Web" None ] [ iface "Web" "calls" "a" "c/shop.yaml"; iface "Api" "serves" "a" "c/shop.yaml" ]
          equal [] (ok (Codegen.tsTargets h None Set.empty)) "no package.json"
          let t = ok (Codegen.tsTargets h None (Set.ofList [ "." ])) |> List.exactlyOne
          equal "." t.Repo "repo"
          equal 2 t.Files.Length "files"
      "codegen ts: two sources with the same stem in one repo are an error", fun () ->
          let h =
              withIfaces [ elem "Api" (Some "Api") ]
                  [ iface "Api" "calls" "a" "c/fare-quotes.yaml"; iface "Api" "calls" "b" "d/fare_quotes.yaml" ]
          match Codegen.tsTargets h (Some [ "Api" ]) Set.empty with
          | Error [ m ] -> check (m.Contains "FareQuotes") m
          | other -> failwithf "%A" other
      "codegen ts: the command is one openapi-typescript line and toolFiles is the schema", fun () ->
          equal [ "openapi-typescript in/contracts/openapi/shop.yaml --output out/Ui/generated/contracts/ts/Shop/Shop.schema.ts" ] (Codegen.commands tsOne) "command"
          equal [ "generated/contracts/ts/Shop/Shop.schema.ts" ] (Codegen.toolFiles tsOne) "tool files"
          let cs = ok (Codegen.targets split None) |> List.exactlyOne
          equal cs.Files (Codegen.toolFiles cs) "csharp: all"
      "codegen ts: the client is golden, stable, and the hash changes only the header", fun () ->
          let a = Codegen.tsClient tsOne "0123456789abcdef"
          equal a (Codegen.tsClient tsOne "0123456789abcdef") "stable"
          check (a.EndsWith "\n" && not (a.Contains "\r") && a |> Seq.forall (fun c -> int c < 128)) "ascii, lf, trailing newline"
          let tail = a.Substring(a.IndexOf "export function createShopClient")
          equal
              ("export function createShopClient(baseUrl: string, fetchImpl: typeof fetch = fetch) {\n  return {\n"
               + "    createOrder: (args: Args<operations[\"createOrder\"]>) => call(fetchImpl, baseUrl, \"POST\", \"/orders\", args as never) as Promise<Ok<operations[\"createOrder\"]>>,\n"
               + "    getOrder: (args: Args<operations[\"getOrder\"]>) => call(fetchImpl, baseUrl, \"GET\", \"/orders/{id}\", args as never) as Promise<Ok<operations[\"getOrder\"]>>,\n"
               + "  };\n}\n")
              tail "functions"
          check (a.StartsWith "// Generated by the Factory from contracts/openapi/shop.yaml (sha256 0123456789ab). Do not edit: generated/contracts/** is read-only.\nimport type { operations } from \"./Shop.schema\";\n") "header"
          let b = Codegen.tsClient tsOne "ffffffffffffffff"
          equal (a.Replace("0123456789ab", "ffffffffffff")) b "only the header differs"
      "codegen ts: an id that is not an identifier is a quoted key", fun () ->
          let t = { tsOne with Operations = [ ({ Id = "get-order"; Method = "GET"; Path = "/o" }: Codegen.ClientOp) ] }
          check ((Codegen.tsClient t "x").Contains "    \"get-order\": (args: Args<operations[\"get-order\"]>)") "quoted"
      "codegen ts: drift reports an added file under generated/contracts/ts", fun () ->
          let baseline = Map.ofList [ "generated/contracts/ts/Shop/Shop.schema.ts", "1" ]
          equal [ "added: generated/contracts/ts/x.ts" ] (Codegen.drift baseline (baseline.Add("generated/contracts/ts/x.ts", "2"))) "added"
          equal [ "modified: generated/contracts/ts/Shop/Shop.schema.ts" ] (Codegen.drift baseline (baseline.Add("generated/contracts/ts/Shop/Shop.schema.ts", "9"))) "modified"
      "codegen: stem is the file name in PascalCase on - _ and .", fun () ->
          equal "FareQuotes" (Codegen.stem "contracts/openapi/fare-quotes.yaml") "dash"
          equal "ShopV2" (Codegen.stem "a/shop_v2.yaml") "underscore"
          equal "OrdersApi" (Codegen.stem "orders.api.yml") "dot"
          equal "Shop" (Codegen.stem "shop.yaml") "plain"
      "codegen: split run, the serving repo gets Dtos and Server and the calling repo nothing by default", fun () ->
          let ts = ok (Codegen.targets split None)
          let t = List.exactlyOne ts
          equal "Api" t.Repo "repo"
          equal "Shop" t.Stem "stem"
          equal "Contracts.Shop" t.Namespace "namespace"
          equal [ Codegen.Server ] t.Sides "sides"
          equal [ "generated/contracts/csharp/Shop/Shop.Dtos.g.cs"; "generated/contracts/csharp/Shop/Shop.Server.g.cs" ] t.Files "files"
      "codegen: policy csharp [Api, Ui] gives the calling repo Dtos and Client", fun () ->
          let ts = ok (Codegen.targets split (Some [ "Api"; "Ui" ]))
          equal [ "Api"; "Ui" ] (ts |> List.map (fun t -> t.Repo)) "sorted by repo"
          let ui = ts |> List.find (fun t -> t.Repo = "Ui")
          equal [ Codegen.Client ] ui.Sides "ui sides"
          equal [ "generated/contracts/csharp/Shop/Shop.Dtos.g.cs"; "generated/contracts/csharp/Shop/Shop.Client.g.cs" ] ui.Files "ui files"
      "codegen: a repo that both serves and calls gets all three files", fun () ->
          let h = withIfaces [ elem "Api" (Some "Api") ] [ iface "Api" "serves" "a" "c/shop.yaml"; iface "Api" "calls" "a" "c/shop.yaml" ]
          let t = ok (Codegen.targets h None) |> List.exactlyOne
          equal [ Codegen.Server; Codegen.Client ] t.Sides "sides"
          equal 3 t.Files.Length "files"
      "codegen: a single-repo run generates for the dot repo", fun () ->
          let h = withIfaces [ elem "Api" None; elem "Web" None ] [ iface "Web" "calls" "a" "c/shop.yaml"; iface "Api" "serves" "a" "c/shop.yaml" ]
          let t = ok (Codegen.targets h None) |> List.exactlyOne
          equal "." t.Repo "repo"
          equal [ Codegen.Server; Codegen.Client ] t.Sides "sides"
      "codegen: an element without code and an event interface are skipped", fun () ->
          let h =
              withIfaces [ elem "Api" (Some "Api") ]
                  [ iface "NoCode" "serves" "a" "c/shop.yaml"; iface "Api" "publishes" "p/E" "c/shop.yaml" ]
          equal [] (ok (Codegen.targets h None)) "nothing"
          equal [] (ok (Codegen.targets baseHandover None)) "no interfaces"
      "codegen: two sources with the same stem in one repo are an error", fun () ->
          let h =
              withIfaces [ elem "Api" (Some "Api") ]
                  [ iface "Api" "serves" "a" "c/fare-quotes.yaml"; iface "Api" "serves" "b" "d/fare_quotes.yaml" ]
          match Codegen.targets h None with
          | Error [ m ] -> check (m.Contains "FareQuotes" && m.Contains "c/fare-quotes.yaml") m
          | other -> failwithf "%A" other
      "codegen: commands are stable and carry the pinned flags", fun () ->
          let t = ok (Codegen.targets (withIfaces [ elem "Api" (Some "Api") ] [ iface "Api" "serves" "a" "c/shop.yaml"; iface "Api" "calls" "a" "c/shop.yaml" ]) None) |> List.exactlyOne
          let cs = Codegen.commands t
          equal
              [ "nswag openapi2csclient /input:in/c/shop.yaml /output:out/Api/generated/contracts/csharp/Shop/Shop.Dtos.g.cs /GenerateClientClasses:false /GenerateDtoTypes:true /JsonLibrary:SystemTextJson /Namespace:Contracts.Shop"
                "nswag openapi2cscontroller /input:in/c/shop.yaml /output:out/Api/generated/contracts/csharp/Shop/Shop.Server.g.cs /ClassName:Shop /ControllerStyle:Partial /ControllerTarget:AspNetCore /GenerateDtoTypes:false /JsonLibrary:SystemTextJson /Namespace:Contracts.Shop"
                "nswag openapi2csclient /input:in/c/shop.yaml /output:out/Api/generated/contracts/csharp/Shop/Shop.Client.g.cs /ClassName:ShopClient /GenerateClientInterfaces:true /GenerateDtoTypes:false /InjectHttpClient:true /UseBaseUrl:false /JsonLibrary:SystemTextJson /Namespace:Contracts.Shop" ]
              cs "commands"
          equal cs (Codegen.commands t) "stable"
      "codegen: drift reports missing, modified and added anywhere under the root, sorted", fun () ->
          let r = Codegen.root
          let baseline = Map.ofList [ $"{r}/A/A.Dtos.g.cs", "1"; $"{r}/A/A.Server.g.cs", "2"; $"{r}/B/B.Dtos.g.cs", "3"; "src/other.cs", "9" ]
          let current = Map.ofList [ $"{r}/A/A.Dtos.g.cs", "1"; $"{r}/A/A.Server.g.cs", "X"; $"{r}/C/extra.cs", "4"; "src/new.cs", "5" ]
          equal [ $"added: {r}/C/extra.cs"; $"missing: {r}/B/B.Dtos.g.cs"; $"modified: {r}/A/A.Server.g.cs" ] (Codegen.drift baseline current) "drift"
          equal [] (Codegen.drift baseline baseline) "none" ]
