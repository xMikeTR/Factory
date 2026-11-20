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

let tests: (string * (unit -> unit)) list =
    [ "codegen: stem is the file name in PascalCase on - _ and .", fun () ->
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
