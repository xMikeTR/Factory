namespace Factory.Core

open System.Text
open System.Text.Json.Nodes

/// API behaviour tests (D7b), pure. From designc's frozen given/then rows (scenarios/2, requests from /3) it renders xUnit tests that
/// seed rows through a per-repo adapter, call the operation through the generated NSwag client, query the rows back and compare
/// them on the listed fields only. Running them is `Factory.S2.Verify.ApiScenarioCheck`; nothing here touches a disk or a tool.
[<RequireQualifiedAccess>]
module ApiScenarios =

    /// Where the generated tests live in a repo (under `Codegen.area`, so seeded, protected and drift-checked with the rest).
    let folder = "generated/contracts/api-tests"

    /// The `dotnet test --filter` that selects exactly the generated tests.
    let filter = "FullyQualifiedName~Contracts.ApiScenarios"

    let runtimeFile = $"{folder}/ScenarioRuntime.g.cs"

    // ---------- the matching semantics (the same text is generated as C below) ----------

    let private json (n: JsonNode) = if isNull n then "null" else n.ToJsonString()

    let private obj (row: string) : JsonObject =
        match JsonNode.Parse row with
        | :? JsonObject as o -> o
        | _ -> failwithf "not a JSON object: %s" row

    /// The mismatches of `expected` rows (JSON object text, as frozen) against the `stored` rows of one entity, one line each. A row
    /// with `id` needs a stored row with that id and each listed field must deep-equal; a row without `id` needs some stored row that
    /// matches on every listed field. Unlisted fields and extra stored rows are not checked, and absence is never asserted.
    let compare (entity: string) (expected: string list) (stored: string list) : string list =
        let storedRows = stored |> List.map obj
        [ for e in expected do
              let want = obj e
              match want.TryGetPropertyValue "id" with
              | true, wantId ->
                  let idText = json wantId
                  let hit =
                      storedRows
                      |> List.tryFind (fun s -> match s.TryGetPropertyValue "id" with | true, sid -> JsonNode.DeepEquals(sid, wantId) | _ -> false)
                  match hit with
                  | None -> yield $"{entity} id={idText}: no stored row"
                  | Some row ->
                      for kv in want do
                          if kv.Key <> "id" then
                              match row.TryGetPropertyValue kv.Key with
                              | true, got when JsonNode.DeepEquals(kv.Value, got) -> ()
                              | true, got -> yield $"{entity} id={idText}: {kv.Key} expected {json kv.Value}, got {json got}"
                              | _ -> yield $"{entity} id={idText}: {kv.Key} expected {json kv.Value}, got <missing>"
              | _ ->
                  let matches (s: JsonObject) =
                      want |> Seq.forall (fun kv -> match s.TryGetPropertyValue kv.Key with | true, g -> JsonNode.DeepEquals(kv.Value, g) | _ -> false)
                  if not (List.exists matches storedRows) then yield $"{entity}: no stored row matches {want.ToJsonString()}" ]

    /// One mismatch line with the scenario named, as the generated runtime throws it.
    let message (id: string) (index: int) (title: string) (line: string) = $"{id} example {index} ({title}): {line}"

    /// The DisplayName of a generated test.
    let displayName (id: string) (index: int) (title: string) = $"{id} example {index}: {title}"

    /// Reads a failed test's DisplayName back: (scenario, example), or None for a test that is not a generated one.
    let parseDisplayName (name: string) : (string * int) option =
        let m = System.Text.RegularExpressions.Regex.Match(name, @"^(\S+) example (\d+): ")
        if m.Success then Some(m.Groups.[1].Value, int m.Groups.[2].Value) else None

    /// What a failure message says went wrong: `rows` (assert mismatch), `call` (the operation answered an error), `seed` (the adapter
    /// threw or is missing). Judged from the first line.
    let classify (message: string) : string =
        let first = (message.Split('\n').[0])
        if first.Contains "): call " then "call"
        elif first.Contains " expected " || first.Contains "no stored row" then "rows"
        else "seed"

    // ---------- rendering ----------

    /// `ORD-1-ts-01` -> `Ord1Ts01`: words on non-alphanumerics, each capitalised, the rest lower-cased; a leading digit gets an `S`.
    let idPascal (id: string) : string =
        let words = System.Text.RegularExpressions.Regex.Split(id, "[^A-Za-z0-9]+") |> Array.filter (fun w -> w <> "")
        let joined = words |> Array.map (fun w -> string (System.Char.ToUpperInvariant w.[0]) + w.Substring(1).ToLowerInvariant()) |> String.concat ""
        if joined.Length > 0 && System.Char.IsDigit joined.[0] then "S" + joined else joined

    /// A C# string literal (regular, not verbatim) for any text.
    let csString (s: string) : string =
        let sb = StringBuilder("\"")
        for c in s do
            match c with
            | '\\' -> sb.Append "\\\\" |> ignore
            | '"' -> sb.Append "\\\"" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append c |> ignore
        sb.Append('"').ToString()

    let private header (what: string) = $"// <auto-generated> Factory X4 {what}. Do not edit."

    /// The runtime every repo's generated tests share: byte-identical text.
    let runtimeText : string =
        String.concat "\n"
            [ header "scenario runtime"
              "#nullable enable"
              "using System;"
              "using System.Collections.Generic;"
              "using System.Globalization;"
              "using System.Linq;"
              "using System.Net.Http;"
              "using System.Reflection;"
              "using System.Text.Json;"
              "using System.Text.Json.Nodes;"
              "using System.Threading;"
              "using System.Threading.Tasks;"
              ""
              "namespace Contracts.ApiScenarios;"
              ""
              "/// The repo's seed/query adapter: implement it once in the test project (outside generated/)."
              "public interface IScenarioAdapter : IAsyncDisposable"
              "{"
              "    /// Starts the system under test, empty, and returns a client whose BaseAddress reaches it."
              "    Task<HttpClient> StartAsync();"
              "    /// Stores these rows for the entity (the qualified id, e.g. orders/Order)."
              "    Task SeedAsync(string entity, IReadOnlyList<JsonObject> rows);"
              "    /// Every stored row of the entity, after the call."
              "    Task<IReadOnlyList<JsonObject>> QueryAsync(string entity);"
              "}"
              ""
              "public sealed class ScenarioSetupException : Exception { public ScenarioSetupException(string message) : base(message) { } }"
              "public sealed class ScenarioMismatch : Exception { public ScenarioMismatch(string message) : base(message) { } }"
              "public sealed class ScenarioCallException : Exception { public ScenarioCallException(string message) : base(message) { } }"
              ""
              "public static class ScenarioRuntime"
              "{"
              "    public static IScenarioAdapter Adapter()"
              "    {"
              "        var found = new List<Type>();"
              "        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())"
              "        {"
              "            Type[] types;"
              "            try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; } catch { continue; }"
              "            found.AddRange(types.Where(t => t.IsClass && !t.IsAbstract && typeof(IScenarioAdapter).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null));"
              "        }"
              "        if (found.Count != 1) throw new ScenarioSetupException($\"adapter: implement IScenarioAdapter once in this test project (found {found.Count})\");"
              "        return (IScenarioAdapter)Activator.CreateInstance(found[0])!;"
              "    }"
              ""
              "    public static IReadOnlyList<JsonObject> Rows(params string[] json) => json.Select(j => JsonNode.Parse(j)!.AsObject()).ToList();"
              ""
              "    public static async Task CallAsync(string id, int index, string title, object client, string clientMethod, string httpMethod, string httpPath,"
              "        IReadOnlyDictionary<string, string> pathArgs, string? bodyJson)"
              "    {"
              "        var prefix = $\"{id} example {index} ({title}): \";"
              "        var method = client.GetType().GetMethods().Where(m => m.Name == clientMethod).OrderByDescending(m => m.GetParameters().Length).FirstOrDefault()"
              "            ?? throw new ScenarioSetupException($\"{prefix}the generated client has no method {clientMethod}\");"
              "        var args = new List<object?>();"
              "        var bodyUsed = false;"
              "        foreach (var p in method.GetParameters())"
              "        {"
              "            var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;"
              "            if (type == typeof(CancellationToken)) args.Add(CancellationToken.None);"
              "            else if (pathArgs.FirstOrDefault(a => string.Equals(a.Key, p.Name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } arg)"
              "                args.Add(type == typeof(Guid) ? Guid.Parse(arg.Value) : Convert.ChangeType(arg.Value, type, CultureInfo.InvariantCulture));"
              "            else if (!bodyUsed && bodyJson is not null) { bodyUsed = true; args.Add(JsonSerializer.Deserialize(bodyJson, p.ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web))); }"
              "            else throw new ScenarioSetupException($\"{prefix}cannot bind parameter {p.Name} of {clientMethod}\");"
              "        }"
              "        try { await (Task)method.Invoke(client, args.ToArray())!; }"
              "        catch (Exception e)"
              "        {"
              "            var inner = e is TargetInvocationException { InnerException: not null } t ? t.InnerException : e;"
              "            var status = inner.GetType().GetProperty(\"StatusCode\")?.GetValue(inner)?.ToString() ?? inner.Message;"
              "            throw new ScenarioCallException($\"{prefix}call {httpMethod} {httpPath} failed: {status}\");"
              "        }"
              "    }"
              ""
              "    private static string Text(JsonNode? n) => n?.ToJsonString() ?? \"null\";"
              ""
              "    public static void Assert(string id, int index, string title, string entity, IReadOnlyList<JsonObject> expected, IReadOnlyList<JsonObject> stored)"
              "    {"
              "        var prefix = $\"{id} example {index} ({title}): \";"
              "        var misses = new List<string>();"
              "        foreach (var want in expected)"
              "        {"
              "            if (want.TryGetPropertyValue(\"id\", out var wantId))"
              "            {"
              "                var row = stored.FirstOrDefault(s => s.TryGetPropertyValue(\"id\", out var sid) && JsonNode.DeepEquals(sid, wantId));"
              "                if (row is null) { misses.Add($\"{prefix}{entity} id={Text(wantId)}: no stored row\"); continue; }"
              "                foreach (var kv in want)"
              "                {"
              "                    if (kv.Key == \"id\") continue;"
              "                    if (!row.TryGetPropertyValue(kv.Key, out var got)) misses.Add($\"{prefix}{entity} id={Text(wantId)}: {kv.Key} expected {Text(kv.Value)}, got <missing>\");"
              "                    else if (!JsonNode.DeepEquals(kv.Value, got)) misses.Add($\"{prefix}{entity} id={Text(wantId)}: {kv.Key} expected {Text(kv.Value)}, got {Text(got)}\");"
              "                }"
              "            }"
              "            else if (!stored.Any(s => want.All(kv => s.TryGetPropertyValue(kv.Key, out var g) && JsonNode.DeepEquals(kv.Value, g))))"
              "                misses.Add($\"{prefix}{entity}: no stored row matches {want.ToJsonString()}\");"
              "        }"
              "        if (misses.Count > 0) throw new ScenarioMismatch(string.Join(\"\\n\", misses));"
              "    }"
              "}"
              "" ]

    let private rowsLiteral (rows: string list) =
        "ScenarioRuntime.Rows(" + (rows |> List.map csString |> String.concat ", ") + ")"

    let private classText (a: ApiScenario) (stem: string) (client: string) : string =
        let examples = a.Examples |> List.filter (fun e -> e.Request.IsSome) |> List.sortBy (fun e -> e.Index)
        let method = Codegen.clientMethod a.OperationId
        let facts =
            [ for e in examples do
                  let req = e.Request.Value
                  let id, title = csString a.Id, csString e.Title
                  let pathArgs =
                      if req.Path.IsEmpty then "new Dictionary<string, string>()"
                      else "new Dictionary<string, string> { " + (req.Path |> List.map (fun (k, v) -> $"[{csString k}] = {csString v}") |> String.concat ", ") + " }"
                  yield $"    [Fact(DisplayName = {csString (displayName a.Id e.Index e.Title)})]"
                  yield $"    public async Task Example{e.Index}()"
                  yield "    {"
                  yield "        await using var a = ScenarioRuntime.Adapter();"
                  yield "        var http = await a.StartAsync();"
                  for (entity, rows) in e.Given do
                      yield $"        await a.SeedAsync({csString entity}, {rowsLiteral rows});"
                  let body = match req.Body with Some b -> csString b | None -> "null"
                  let httpMethod = csString (a.Method.ToUpperInvariant())
                  yield $"        await ScenarioRuntime.CallAsync({id}, {e.Index}, {title}, new {client}(http), {csString method}, {httpMethod}, {csString a.OpPath}, {pathArgs}, {body});"
                  for (entity, rows) in e.Then do
                      yield $"        ScenarioRuntime.Assert({id}, {e.Index}, {title}, {csString entity}, {rowsLiteral rows}, await a.QueryAsync({csString entity}));"
                  yield "    }" ]
        String.concat "\n"
            ([ header $"from {a.Path} sha256 {a.Sha256.Substring(0, min 12 a.Sha256.Length)}"
               "#nullable enable"
               "using System.Collections.Generic;"
               "using System.Threading.Tasks;"
               "using Xunit;"
               ""
               "namespace Contracts.ApiScenarios;"
               ""
               $"public sealed class {idPascal a.Id}"
               "{" ]
             @ facts
             @ [ "}"; "" ])

    /// Every generated file as (repo, repo-relative path, text), sorted by repo then path, or the handover errors that name a scenario.
    /// Scenarios without a request on any example generate nothing (until designc scenarios/3 carries it).
    let files (h: Handover) : Result<(string * string * string) list, string list> =
        let specs = h.Elements |> List.map (fun e -> e.Id.Value, e) |> Map.ofList
        let results =
            [ for a in h.ApiScenarios do
                  if Codegen.hasGeneratedExamples a then
                      match Codegen.apiServing h a with
                      | Error e -> yield Error e
                      | Ok i ->
                          match Map.tryFind i.Element specs with
                          | None -> yield Error $"API scenario {a.Id}: the serving element {i.Element} is not in the handover"
                          | Some el ->
                              let st = Codegen.stem i.File.Value
                              let repo = defaultArg el.Repo "."
                              yield Ok(repo, $"{folder}/{st}/{idPascal a.Id}.ApiScenario.g.cs", classText a st $"global::Contracts.{st}.{st}Client") ]
        let errors = results |> List.choose (function Error e -> Some e | Ok _ -> None)
        if not errors.IsEmpty then Error errors
        else
            let tests = results |> List.choose (function Ok f -> Some f | Error _ -> None)
            let runtime = tests |> List.map (fun (r, _, _) -> r) |> List.distinct |> List.map (fun r -> r, runtimeFile, runtimeText)
            Ok((runtime @ tests) |> List.sortWith (fun (r1, p1, _) (r2, p2, _) ->
                match System.String.CompareOrdinal(r1, r2) with
                | 0 -> System.String.CompareOrdinal(p1, p2)
                | c -> c))

    /// The brief section naming the generated API tests that judge these elements (None: all), or None when there are none.
    /// `files` are the manifest's (repo, path, source). A stable text: it names the folder, the adapter interface and the rule.
    let briefSection (h: Handover) (files: (string * string * string) list) (elements: string list option) : string option =
        let specs = h.Elements |> List.map (fun e -> e.Id.Value, e) |> Map.ofList
        let ids =
            [ for a in h.ApiScenarios do
                  match Codegen.apiServing h a with
                  | Ok i when (match elements with None -> true | Some l -> List.contains i.Element l) ->
                      let repo = match Map.tryFind i.Element specs with Some e -> defaultArg e.Repo "." | None -> "."
                      let name = "/" + idPascal a.Id + ".ApiScenario.g.cs"
                      if files |> List.exists (fun (r, p, _) -> r = repo && p.StartsWith(folder + "/", System.StringComparison.Ordinal) && p.EndsWith(name, System.StringComparison.Ordinal)) then
                          yield a.Id
                  | _ -> () ]
            |> List.distinct
            |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))
        if ids.IsEmpty then None
        else
            let joined = String.concat ", " ids
            Some(
                "## Generated API tests (read-only; the Factory wrote them)\n\n"
                + $"The API behaviour tests for {joined} are generated under `{folder}/`. They seed rows through `IScenarioAdapter` (declared in `{runtimeFile}`), call the operation through the generated client, and compare the stored rows. "
                + "Implement `IScenarioAdapter` once in the repo's test project, outside `generated/`, and make that project compile `generated/contracts/api-tests/**/*.cs` and reference a JUnit logger (`JunitXml.TestLogger`): integration verify reads its results from `$FACTORY_TEST_RESULTS`. "
                + "The generated tests are read-only and must pass in integration verify.\n")
