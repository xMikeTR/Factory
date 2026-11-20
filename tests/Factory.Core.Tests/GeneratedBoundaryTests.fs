module Factory.Core.Tests.GeneratedBoundaryTests

open Factory.Core
open Factory.Core.Tests.Support

let private src = "contracts/openapi/shop.yaml"
let private op id m p : GeneratedBoundary.Op = { Source = src; Id = id; Method = m; Path = p }
let private ops = [ op (Some "listOrders") "GET" "/orders"; op (Some "getOrder") "GET" "/orders/{id}"; op (Some "createOrder") "POST" "/orders"; op None "GET" "/{id}" ]
let private dtos = [ src, "Order" ]
let private one file text = [ file, [ 1, text ] ]
let private subjects f = f |> List.map (fun (x: GeneratedBoundary.Finding) -> x.Kind, x.Subject)
let private find file text = GeneratedBoundary.findings ops dtos (one file text)

let tests: (string * (unit -> unit)) list =
    [ "boundary: fetch with a concatenated path, a template literal and a C# GetFromJsonAsync are flagged",
      fun () ->
          equal [ "client", "listOrders or createOrder" ] (subjects (find "src/a.ts" "fetch(\"/orders\")")) "plain"
          equal [ "client", "listOrders or getOrder or createOrder" ] (subjects (find "src/a.ts" "const r = await fetch(\"/orders/\" + id)")) "concatenated"
          equal [ "client", "getOrder" ] (subjects (find "src/a.ts" "await fetch(`${base}/orders/${id}`)")) "template literal"
          equal [ "client", "listOrders or createOrder" ] (subjects (find "src/A.cs" "var o = await http.GetFromJsonAsync<Order[]>(\"orders?x=1\");")) "C#, no leading slash, query"
      "boundary: lines without a marker or in non-code files are not flagged",
      fun () ->
          equal [] (find "src/r.tsx" "<Route path=\"/orders\" element={<Orders />} />") "router"
          equal [] (find "src/C.cs" "[HttpGet(\"orders\")]") "controller attribute"
          equal [] (find "src/a.md" "fetch(\"/orders\")") "markdown"
      "boundary: generated files are not judged",
      fun () -> equal [] (find "generated/contracts/ts/Shop/Shop.client.ts" "fetch(\"/orders\")") "generated"
      "boundary: a template with no static segment never matches",
      fun () -> equal [] (GeneratedBoundary.findings [ op None "GET" "/{id}" ] [] (one "a.ts" "fetch(\"/anything\")")) "only a param"
      "boundary: a DTO is flagged only for a source without a server side",
      fun () ->
          equal [ "dto", "Order" ] (subjects (find "src/o.ts" "export interface Order { id: string }")) "calling side"
          equal [ "dto", "Order" ] (subjects (find "src/O.cs" "public sealed record Order(string Id);")) "C#"
          equal [] (GeneratedBoundary.findings ops [] (one "src/o.ts" "export interface Order { id: string }")) "serving repo: domain type is legitimate"
          equal [] (find "src/o.ts" "export interface Customer { id: string }") "another name"
      "boundary: two operations on one path make one finding naming both, and output is sorted",
      fun () ->
          let f =
              GeneratedBoundary.findings ops dtos
                  [ "src/b.ts", [ 5, "fetch(\"/orders\")" ]
                    "src/a.ts", [ 9, "interface Order {}"; 2, "axios.get(\"/orders\")" ] ]
          equal [ "src/a.ts", 2; "src/a.ts", 9; "src/b.ts", 5 ] (f |> List.map (fun x -> x.File, x.Line)) "sorted"
          equal 1 (f |> List.filter (fun x -> x.File = "src/b.ts") |> List.length) "one finding for the line"
      "boundary: the line names the file, the operation and the generated symbol",
      fun () ->
          equal "src/a.ts:1: hand-written client call for getOrder (GET /orders/{id}); use `createShopClient(baseUrl).getOrder` from `generated/contracts/ts/Shop/Shop.client.ts`"
              (GeneratedBoundary.line (find "src/a.ts" "fetch(`/orders/${id}`)").Head) "client line"
          equal "src/O.cs:1: hand-written DTO `Order`; use `Contracts.Shop.Order` from `generated/contracts/csharp/Shop/Shop.Dtos.g.cs`"
              (GeneratedBoundary.line (find "src/O.cs" "class Order {}").Head) "dto line"
          check ((GeneratedBoundary.line (find "src/o.ts" "type Order = {}").Head).Contains "components[\"schemas\"][\"Order\"]") "ts dto hint" 
      "boundary: addedLines keeps the text and numbers of the added lines per file",
      fun () ->
          let diff = "diff --git a/x.ts b/x.ts\n--- a/x.ts\n+++ b/x.ts\n@@ -1,2 +1,3 @@\n keep\n+fetch(\"/orders\")\n+second\n"
          equal [ "x.ts", [ 2, "fetch(\"/orders\")"; 3, "second" ] ] (Diff.addedLines diff) "added" ]
