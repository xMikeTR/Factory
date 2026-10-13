module Factory.Core.Tests.DesignTokenTests

open Factory.Core
open Factory.Core.Tests.Support

let private p = PathPattern.createOrFail
let private task id writes =
    { Id = TaskId id; Title = ""; Instructions = ""; Elements = []; Writes = writes |> List.map p; TestWrites = []
      DependsOn = []; Covers = []; Verify = [] }

let private json =
    """{ "font": { "$type": "dimension", "size": { "body": { "$value": "16px" }, "small": { "$value": "12px" } } },
         "color": { "brand": { "$type": "color", "$value": "#0a5" } } }"""

let tests: (string * (unit -> unit)) list =
    [ "tokens: groups flatten to dashed names and inherit $type", fun () ->
          let t = DesignTokens.parse json
          equal
              (Ok [ "font-size-body", "dimension", "16px"; "font-size-small", "dimension", "12px"; "color-brand", "color", "#0a5" ])
              (t |> Result.map (List.map (fun x -> x.Name, x.Type, x.Value)))
              "tokens"
      "tokens: a value with no type is rejected", fun () ->
          check (DesignTokens.parse """{ "gap": { "$value": "4px" } }""" |> Result.isError) "anonymous"
      "tokens: generated css has one custom property per token", fun () ->
          let css = DesignTokens.parse json |> Result.map DesignTokens.toCss
          check (match css with Ok c -> c.Contains "--font-size-body: 16px;" && c.Contains "--color-brand: #0a5;" | _ -> false) "css"
      "tokens: text-size tokens under the minimum are named", fun () ->
          let low = DesignTokens.parse json |> Result.map DesignTokens.belowMinimumText |> Result.map (List.map (fun t -> t.Name))
          equal (Ok [ "font-size-small" ]) low "below"
      "lint: a raw value is a translation error on the task that writes the file, orphans reported apart", fun () ->
          let tasks = [ task "T-001" [ "src/ui/**" ]; task "T-002" [ "src/api/**" ] ]
          let v file = { File = file; Line = 3; Rule = "declaration-strict-value"; Text = "#fff" }
          let errors, orphans = TokenLint.translationErrors tasks [ v "src/ui/Quote.css"; v "src/ui/Other.css"; v "web/x.css" ]
          equal [ TranslationError(TaskId "T-001") ] errors "errors"
          equal [ "web/x.css" ] orphans "orphans" ]
