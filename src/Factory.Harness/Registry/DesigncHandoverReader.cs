using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Factory.Core;
using Microsoft.FSharp.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Registry;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// A handover frozen by Designc (handovers/&lt;id&gt;/): handover.yaml names elements.json and the bound
/// decisions and lists every file with its hash16. elements.json is Designc's resolved model (its
/// docs/handover-elements.schema.json), so the DSL is never parsed here; only elements that declare
/// code are code-backed and appear in the Handover.
public sealed partial class DesigncHandoverReader : IHandoverSource
{
    public async Task<Handover> LoadAsync(string handoverDir, S2Config config, CancellationToken ct)
    {
        var errors = new List<string>();
        var manifestPath = Path.Combine(handoverDir, "handover.yaml");
        if (!File.Exists(manifestPath)) Reject(handoverDir, ["handover.yaml is missing"]);

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        Dictionary<string, object?> manifest;
        try { manifest = Yaml.Deserialize<Dictionary<string, object?>>(Encoding.UTF8.GetString(manifestBytes)) ?? []; }
        catch (YamlDotNet.Core.YamlException e) { Reject(handoverDir, [$"handover.yaml is not readable: {e.Message}"]); return null!; }

        var id = Str(manifest, "id");
        if (id is null) errors.Add("handover.yaml: id is required");

        // Integrity: every frozen file must still hash to what Designc recorded.
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in List(manifest, "files").OfType<Dictionary<object, object?>>())
        {
            var rel = entry.GetValueOrDefault("path") as string;
            var hash = entry.GetValueOrDefault("hash")?.ToString();
            if (rel is null || hash is null) { errors.Add("handover.yaml: every file needs path and hash"); continue; }
            var full = Path.Combine(handoverDir, rel);
            if (!File.Exists(full)) { errors.Add($"{rel} is listed in handover.yaml but missing"); continue; }
            var text = await File.ReadAllTextAsync(full, ct);
            if (Hash16(text) != hash) errors.Add($"{rel} does not match its frozen hash (changed after the handover was frozen)");
            files[rel] = text;
        }

        var apiContracts = ApiContracts(manifest, files, errors);
        var elementsPath = Str(manifest, "elements");
        var resolved = elementsPath is null
            ? Fail("handover.yaml names no elements file; re-freeze it with a designc that writes elements.json")
            : files.TryGetValue(elementsPath, out var json) ? ParseElements(json, apiContracts, errors)
            : Fail($"handover.yaml: elements '{elementsPath}' is not among the frozen files");

        var elements = resolved is null ? [] : Elements(resolved.Value, errors);
        var byName = resolved?.Elements.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Id) ?? [];

        var decisions = new List<BoundDecision>();
        foreach (var did in List(manifest, "decisions").Select(d => d?.ToString()).OfType<string>())
        {
            var rel = files.Keys.FirstOrDefault(k => Regex.IsMatch(k, $@"^decisions/{Regex.Escape(did)}\.ya?ml$"));
            if (rel is null) { errors.Add($"decision {did} is listed but its file is not frozen"); continue; }
            decisions.Add(Decision(did, Yaml.Deserialize<Dictionary<string, object?>>(files[rel]) ?? [], byName, errors));
        }

        var vocabulary = resolved?.Events ?? [];

        var protectedPaths = Patterns(config.ProtectedPaths, "S2Config.ProtectedPaths", errors)
            .Concat(Patterns(resolved?.Reserved ?? [], "elements.json reserved", errors))
            .DistinctBy(p => p.Value).ToList();

        var acceptanceSpecs = AcceptanceSpecs(manifest, errors);
        var tokens = Tokens(manifest, files, errors);
        var criteria = Criteria(manifest, files, errors);
        var loopSpecs = LoopSpecs(manifest, errors);
        var references = References(manifest, errors);
        var (contracts, uncovered) = Contracts(manifest, files, errors);
        var ui = UiScreens(manifest, files, errors);
        var tests = Tests(manifest, files, errors);
        var (scenarioTests, scenarioSkipped) = ScenarioTests(manifest, files, errors);

        if (errors.Count > 0) Reject(handoverDir, errors);
        return new Handover(
            id!,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            Core.ToList(elements),
            Core.ToList(decisions),
            Core.ToList(protectedPaths),
            Core.ToList(vocabulary),
            Core.ToList(Relationships(resolved!.Value, elements)),
            Core.ToList(acceptanceSpecs),
            Str(manifest, "work_item") is { } workItem ? FSharpOption<string>.Some(workItem) : FSharpOption<string>.None,
            tokens,
            Str(manifest, "design_version") is { } version ? FSharpOption<string>.Some(version) : FSharpOption<string>.None,
            Core.ToList(criteria),
            Core.ToList(loopSpecs),
            Core.ToList(references),
            Core.ToList(contracts),
            Core.ToList(uncovered),
            Core.ToList(apiContracts),
            Str(manifest, "requirement_risk") switch
            {
                "low" => FSharpOption<RiskLevel>.Some(RiskLevel.Low),
                "medium" => FSharpOption<RiskLevel>.Some(RiskLevel.Medium),
                "high" or "critical" => FSharpOption<RiskLevel>.Some(RiskLevel.High),
                _ => FSharpOption<RiskLevel>.None
            },
            Str(manifest, "requirement_data_class") is ("public" or "internal" or "regulated") and { } dataClass
                ? FSharpOption<string>.Some(dataClass) : FSharpOption<string>.None,
            Core.ToList(ui),
            tests,
            Core.ToList(scenarioTests),
            Core.ToList(scenarioSkipped),
            Core.ToList(resolved!.Value.Interfaces),
            Core.ToList(apiContracts.OrderBy(c => c, StringComparer.Ordinal).Select(c => new UiText(c, Sha256(files[c]), files[c]))));

        ResolvedModel? Fail(string error) { errors.Add(error); return null; }
    }

    /// `ui`: the derived ui.json (designc.handover.ui/1). Each text copy it names must be a frozen file whose sha256 matches, each
    /// screen must be listed in `ui_screens`, and a spec must name the file its page lives in (the page test is always generated).
    /// The preview pictures are binary, so they are not in `files`: HandoverInput checks them against `Previews` when it freezes.
    private static List<UiScreen> UiScreens(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        var screens = new List<UiScreen>();
        if (Str(manifest, "ui") is not { } path) return screens;
        if (!files.TryGetValue(path, out var json)) { errors.Add($"handover.yaml: ui '{path}' is not among the frozen files"); return screens; }
        var listed = List(manifest, "ui_screens").Select(x => x?.ToString()).OfType<string>().ToHashSet();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetString() != "designc.handover.ui/1") { errors.Add($"{path}: schema is not designc.handover.ui/1"); return screens; }
            foreach (var sc in root.GetProperty("screens").EnumerateArray())
            {
                var name = sc.GetProperty("screen").GetString()!;
                if (!listed.Contains(name)) errors.Add($"{path}: screen '{name}' is not in handover.yaml ui_screens");
                UiText? Text(JsonElement f, string what)
                {
                    var p = f.GetProperty("path").GetString()!;
                    var want = f.GetProperty("sha256").GetString()!;
                    if (!files.TryGetValue(p, out var text)) { errors.Add($"{path}: {what} '{p}' is not among the frozen files"); return null; }
                    if (Sha256(text) != want) { errors.Add($"{p} does not match the sha256 {path} names for it ({what} of screen '{name}')"); return null; }
                    return new UiText(p, want, text);
                }
                var spec = Text(sc.GetProperty("spec"), "spec");
                var template = Text(sc.GetProperty("template"), "template");
                var components = sc.GetProperty("components").EnumerateArray().Select(c => Text(c, $"component {c.GetProperty("name").GetString()}")).ToList();
                string? component = null, file = null;
                if (spec is not null)
                {
                    using var specDoc = JsonDocument.Parse(spec.Text);
                    component = specDoc.RootElement.TryGetProperty("component", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    file = specDoc.RootElement.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } v ? v : null;
                    if (file is null) errors.Add($"spec {spec.Path} has no file, so the page test cannot be generated");
                    if (component is null) errors.Add($"spec {spec.Path} has no component");
                }
                var previews = sc.GetProperty("previews").GetProperty("files").EnumerateObject()
                    .Select(p => (p.Name, p.Value.GetString()!)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var holdout = sc.GetProperty("holdout") is { ValueKind: JsonValueKind.Object } h ? h.GetProperty("sha256").GetString() : null;
                var cases = sc.GetProperty("holdout_cases").EnumerateArray()
                    .Select(c => new UiHoldoutCase(c.GetProperty("criterion").GetString()!, c.GetProperty("case").GetInt32(), c.GetProperty("state").GetString()!, c.GetProperty("sha256").GetString()!)).ToList();
                if (spec is null || template is null || components.Any(c => c is null) || component is null || file is null) continue;
                screens.Add(new UiScreen(name, component, file, spec, template, Core.ToList(components.Select(c => c!)), Core.ToList(previews.Select(p => Tuple.Create(p.Name, p.Item2))),
                    holdout is null ? FSharpOption<string>.None : FSharpOption<string>.Some(holdout), Core.ToList(cases)));
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { errors.Add($"{path}: {e.Message}"); }
        return screens;
    }

    /// `scenario_tests`: the derived scenarios.json (designc.handover.scenarios/1). Empty when handover.yaml has no such key. Each screen's
    /// generated test must be a frozen file whose sha256 matches; the Factory runs the text as written and never regenerates it.
    private static (List<ScenarioScreen>, List<Tuple<string, string>>) ScenarioTests(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        var screens = new List<ScenarioScreen>();
        var skipped = new List<Tuple<string, string>>();
        if (Str(manifest, "scenario_tests") is not { } path) return (screens, skipped);
        if (!files.TryGetValue(path, out var json)) { errors.Add($"handover.yaml: scenario_tests '{path}' is not among the frozen files"); return (screens, skipped); }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetString() != "designc.handover.scenarios/1") { errors.Add($"{path}: schema is not designc.handover.scenarios/1"); return (screens, skipped); }
            foreach (var sc in root.GetProperty("screens").EnumerateArray())
            {
                var name = sc.GetProperty("screen").GetString()!;
                var test = sc.GetProperty("test");
                var p = test.GetProperty("path").GetString()!;
                var want = test.GetProperty("sha256").GetString()!;
                if (!files.TryGetValue(p, out var text)) { errors.Add($"{path}: scenario test '{p}' is not among the frozen files"); continue; }
                if (Sha256(text) != want) { errors.Add($"{p} does not match the sha256 {path} names for it (scenario test of screen '{name}')"); continue; }
                var scenarios = sc.GetProperty("scenarios").EnumerateArray().Select(c =>
                {
                    var op = c.GetProperty("operation");
                    return new ScenarioRef(c.GetProperty("id").GetString()!,
                        Tuple.Create(op.GetProperty("id").GetString()!, op.GetProperty("method").GetString()!, op.GetProperty("path").GetString()!),
                        Core.ToList(c.GetProperty("examples").EnumerateArray().Select(e => Tuple.Create(e.GetProperty("index").GetInt32(), e.GetProperty("title").GetString()!))));
                }).ToList();
                screens.Add(new ScenarioScreen(name, sc.GetProperty("component").GetString()!, new UiText(p, want, text), Core.ToList(scenarios)));
            }
            foreach (var k in root.GetProperty("skipped").EnumerateArray())
                skipped.Add(Tuple.Create(k.GetProperty("scenario").GetString()!, k.GetProperty("reason").GetString()!));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { errors.Add($"{path}: {e.Message}"); }
        return (screens, skipped);
    }

    /// `tests`: the derived tests.json (designc.handover.tests/1). None when handover.yaml has no `tests` key; any other schema is refused.
    private static FSharpOption<TestMap> Tests(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        if (Str(manifest, "tests") is not { } path) return FSharpOption<TestMap>.None;
        if (!files.TryGetValue(path, out var json)) { errors.Add($"handover.yaml: tests '{path}' is not among the frozen files"); return FSharpOption<TestMap>.None; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetString() != "designc.handover.tests/1") { errors.Add($"{path}: schema is not designc.handover.tests/1"); return FSharpOption<TestMap>.None; }
            static FSharpOption<string> Opt(JsonElement e, string key) =>
                e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? FSharpOption<string>.Some(v.GetString()!) : FSharpOption<string>.None;
            Microsoft.FSharp.Collections.FSharpList<NamedTest> Named(JsonElement arr) => Core.ToList(arr.EnumerateArray()
                .Select(t => new NamedTest(t.GetProperty("kind").GetString()!, t.GetProperty("path").GetString()!, Opt(t, "command"), Opt(t, "via"))));
            var elements = root.GetProperty("elements").EnumerateArray()
                .Select(e => new ElementTests(e.GetProperty("element").GetString()!, Opt(e, "repo"), Named(e.GetProperty("fail_to_pass")), Named(e.GetProperty("pass_to_pass")))).ToList();
            var unmapped = root.GetProperty("unmapped").EnumerateArray()
                .Select(u => Tuple.Create(u.GetProperty("element").GetString()!, u.GetProperty("reason").GetString()!)).ToList();
            return FSharpOption<TestMap>.Some(new TestMap(Core.ToList(elements), Core.ToList(unmapped)));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { errors.Add($"{path}: {e.Message}"); return FSharpOption<TestMap>.None; }
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// The criteria `acceptance` lists, each from its frozen `acceptance/<id>.yaml` (its `element` key says what it is about).
    private static List<AcceptanceCriterion> Criteria(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        var criteria = new List<AcceptanceCriterion>();
        foreach (var id in List(manifest, "acceptance").Select(a => a?.ToString()).OfType<string>())
        {
            var rel = files.Keys.FirstOrDefault(k => Regex.IsMatch(k, $@"^acceptance/{Regex.Escape(id)}\.ya?ml$"));
            if (rel is null) { errors.Add($"acceptance criterion {id} is listed but its file is not frozen"); continue; }
            var element = Yaml.Deserialize<Dictionary<string, object?>>(files[rel])?.GetValueOrDefault("element")?.ToString();
            if (element is null) { errors.Add($"{rel}: element is required"); continue; }
            criteria.Add(new AcceptanceCriterion(id, element, files[rel]));
        }
        return criteria;
    }

    /// `contracts`: `{result, agents: {<agent id>: <policy path>}, uncontracted: [ids]}` (absent in an older handover). Each
    /// policy must be a frozen file (so it was hash-checked above). `Uncovered` lists the agents in `agents.json` that act on
    /// their own (any autonomy but `suggest`) with neither a policy nor a place in `uncontracted`.
    private static (List<AgentContract>, List<string>) Contracts(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        var contracts = new List<AgentContract>();
        var section = manifest.GetValueOrDefault("contracts") as Dictionary<object, object?>;
        if (section?.GetValueOrDefault("agents") is Dictionary<object, object?> map)
            foreach (var (k, v) in map)
            {
                var policy = v?.ToString();
                if (policy is null || !files.TryGetValue(policy, out var text)) { errors.Add($"handover.yaml: the contract for {k} names '{policy}', which is not among the frozen files"); continue; }
                contracts.Add(new AgentContract(k.ToString()!, policy, Hash16(text)));
            }
        var uncontracted = (section?.GetValueOrDefault("uncontracted") as List<object?> ?? []).Select(u => u?.ToString()).OfType<string>().ToHashSet();
        var uncovered = new List<string>();
        var agentsPath = Str(manifest, "agents");
        if (agentsPath is not null && files.TryGetValue(agentsPath, out var agentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(agentsJson);
                foreach (var a in doc.RootElement.GetProperty("agents").EnumerateArray())
                {
                    var id = a.GetProperty("id").GetString()!;
                    var suggest = a.TryGetProperty("autonomy", out var au) && au.GetString() == "suggest";
                    if (!suggest && !uncontracted.Contains(id) && !contracts.Any(c => c.Agent == id)) uncovered.Add(id);
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { errors.Add($"agents.json: {e.Message}"); }
        }
        return (contracts, uncovered);
    }

    /// `references`: `[{key, kind}]`, no paths. A key is a plain name and appears once.
    private static List<Reference> References(Dictionary<string, object?> manifest, List<string> errors)
    {
        var refs = new List<Reference>();
        foreach (var entry in List(manifest, "references").OfType<Dictionary<object, object?>>())
        {
            var key = entry.GetValueOrDefault("key")?.ToString();
            var kind = entry.GetValueOrDefault("kind")?.ToString();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(kind)) { errors.Add("handover.yaml: every reference needs key and kind"); continue; }
            if (refs.Any(r => r.Key == key)) { errors.Add($"handover.yaml: reference '{key}' is listed twice"); continue; }
            refs.Add(new Reference(key, kind));
        }
        return refs;
    }

    /// `contracts_api`: OpenAPI files the handover pins. Each must be a frozen file (so it was hash-checked above).
    /// Not `contracts`, which holds compiled agent policies. Absent in an older handover.
    private static List<string> ApiContracts(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        var paths = List(manifest, "contracts_api").OfType<string>().ToList();
        foreach (var c in paths.Where(c => !files.ContainsKey(c))) errors.Add($"handover.yaml: contracts_api '{c}' is not among the frozen files");
        return paths;
    }

    /// `loop_specs`: each must be one of the frozen `specs`, so the agent's loop never runs a file the gate did not freeze.
    private static List<string> LoopSpecs(Dictionary<string, object?> manifest, List<string> errors)
    {
        var specs = List(manifest, "specs").OfType<string>().ToHashSet();
        var loop = List(manifest, "loop_specs").OfType<string>().ToList();
        foreach (var l in loop.Where(l => !specs.Contains(l))) errors.Add($"handover.yaml: loop spec '{l}' is not among the frozen specs");
        return loop;
    }

    /// The frozen token file named by `tokens`, as text; absent when the handover names none.
    private static FSharpOption<string> Tokens(Dictionary<string, object?> manifest, Dictionary<string, string> files, List<string> errors)
    {
        if (Str(manifest, "tokens") is not { } path) return FSharpOption<string>.None;
        if (files.TryGetValue(path, out var text)) return FSharpOption<string>.Some(text);
        errors.Add($"handover.yaml: tokens '{path}' is not among the frozen files");
        return FSharpOption<string>.None;
    }

    /// The `specs` paths with the hash `files` records for each (the integrity loop above already proved the text).
    private static List<AcceptanceSpec> AcceptanceSpecs(Dictionary<string, object?> manifest, List<string> errors)
    {
        var hashes = List(manifest, "files").OfType<Dictionary<object, object?>>()
            .Where(f => f.GetValueOrDefault("path") is string && f.GetValueOrDefault("hash") is not null)
            .ToDictionary(f => (string)f["path"]!, f => f["hash"]!.ToString()!, StringComparer.Ordinal);
        var specs = new List<AcceptanceSpec>();
        foreach (var path in List(manifest, "specs").OfType<string>())
        {
            if (hashes.TryGetValue(path, out var hash)) specs.Add(new AcceptanceSpec(path, hash));
            else errors.Add($"handover.yaml: spec '{path}' is not among the frozen files");
        }
        return specs;
    }

    /// How a decision's check runs, from its command: `p check` is the P checker, `designc check`
    /// is the model lint, anything else is a script.
    public static string CheckKind(string run) =>
        run.TrimStart() switch
        {
            var r when r.StartsWith("p check", StringComparison.Ordinal) => "p",
            var r when r.StartsWith("designc check", StringComparison.Ordinal) => "lint",
            _ => "script",
        };

    /// Designc's hash16 (rules.py / core.js): two 32-bit FNV-1a passes over the UTF-16 code units.
    public static string Hash16(string s) => Factory.Core.Hash16.compute(s);

    /// One end of a typed relationship (/3 `interfaces`). `schema`/`fields` are not read: codegen reads the OpenAPI file.
    private sealed record InterfaceJson(string? Role, RelationshipJson? Rel, string? Kind, string? Id, string? Method, string? Path, string? File, string? Pack, string? Name, string? Version);
    private sealed record ElementJson(string Id, string Name, string Kind, List<string>? Code, string? Risk, [property: JsonPropertyName("in_scope")] bool? InScope, string? Repo = null, List<InterfaceJson>? Interfaces = null);
    private sealed record RelationshipJson(string From, string To);
    private sealed record ElementsJson(string? Schema, List<ElementJson>? Elements, List<RelationshipJson>? Relationships, List<string>? Events, List<string>? Reserved);
    private readonly record struct ResolvedModel(List<ElementJson> Elements, List<RelationshipJson> Relationships, List<string> Events, List<string> Reserved, List<ElementInterface> Interfaces);

    /// /1 is the design handover; /2 is an item handover (adds `in_scope` per element and `reserved` paths); /3 adds typed `interfaces`.
    private static readonly string[] ElementsSchemas = ["designc.handover.elements/1", "designc.handover.elements/2", "designc.handover.elements/3"];

    private static readonly string[] InterfaceRoles = ["calls", "serves", "publishes", "consumes"];

    private static List<ElementInterface> Interfaces(List<ElementJson> elements, List<string> apiContracts, List<string> errors)
    {
        var all = new List<ElementInterface>();
        foreach (var e in elements)
            foreach (var i in e.Interfaces ?? [])
            {
                var where = $"element '{e.Id}' interface '{i.Id}'";
                if (i.Role is null || !InterfaceRoles.Contains(i.Role)) { errors.Add($"elements.json: {where}: role '{i.Role}' is not calls, serves, publishes or consumes"); continue; }
                var wantKind = i.Role is "calls" or "serves" ? "operation" : "event";
                if (i.Kind != wantKind) { errors.Add($"elements.json: {where}: role '{i.Role}' needs kind '{wantKind}', not '{i.Kind}'"); continue; }
                if (i.Rel is null || string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Version)) { errors.Add($"elements.json: {where}: rel, id and version are required"); continue; }
                if (i.Kind == "operation" && (i.File is null || !apiContracts.Contains(i.File)))
                { errors.Add($"elements.json: {where}: file '{i.File}' is not one of contracts_api"); continue; }
                all.Add(new ElementInterface(e.Id, i.Role, i.Rel.From, i.Rel.To, i.Kind, i.Id,
                    i.Method is null ? FSharpOption<string>.None : FSharpOption<string>.Some(i.Method),
                    i.Path is null ? FSharpOption<string>.None : FSharpOption<string>.Some(i.Path),
                    i.File is null ? FSharpOption<string>.None : FSharpOption<string>.Some(i.File),
                    i.Version));
            }
        foreach (var g in all.GroupBy(i => (i.From, i.To, i.Id)).Where(g => g.Select(i => i.Version).Distinct().Count() > 1))
            errors.Add($"elements.json: {g.Key.Id} on {g.Key.From} → {g.Key.To}: the two ends disagree on version");
        return all;
    }

    private static ResolvedModel? ParseElements(string json, List<string> apiContracts, List<string> errors)
    {
        ElementsJson? doc;
        try { doc = JsonSerializer.Deserialize<ElementsJson>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { errors.Add($"elements.json: {ex.Message}"); return null; }
        if (doc?.Schema is null || !ElementsSchemas.Contains(doc.Schema)) { errors.Add($"elements.json: schema '{doc?.Schema}' is not {string.Join(" or ", ElementsSchemas)}"); return null; }
        if (doc.Schema != "designc.handover.elements/3" && (doc.Elements ?? []).Any(e => e.Interfaces is not null))
        { errors.Add($"elements.json: interfaces are only allowed in designc.handover.elements/3, not {doc.Schema}"); return null; }
        var elements = doc.Elements ?? [];
        return new ResolvedModel(elements, doc.Relationships ?? [], doc.Events ?? [], doc.Reserved ?? [], Interfaces(elements, apiContracts, errors));
    }

    /// The model's relationships between elements that own code (the others cannot be crossed by a task).
    private static List<Relationship> Relationships(ResolvedModel model, List<ElementSpec> owners)
    {
        var ids = owners.Select(o => o.Id.Value).ToHashSet();
        return model.Relationships
            .Where(r => ids.Contains(r.From) && ids.Contains(r.To))
            .Select(r => new Relationship(ElementId.NewElementId(r.From), ElementId.NewElementId(r.To)))
            .Distinct().ToList();
    }

    private static List<ElementSpec> Elements(ResolvedModel model, List<string> errors)
    {
        var specs = new List<ElementSpec>();
        foreach (var e in model.Elements)
        {
            if (e.Code is null) continue;
            var risk = e.Risk switch
            {
                "low" => RiskLevel.Low,
                "medium" => RiskLevel.Medium,
                "high" => RiskLevel.High,
                null => Fail<RiskLevel>($"element '{e.Name}' declares code but no risk"),
                var other => Fail<RiskLevel>($"element '{e.Name}': risk '{other}' is not low, medium or high"),
            };
            var areas = Patterns(e.Code, $"element '{e.Name}' code", errors);
            if (risk is not null) specs.Add(new ElementSpec(ElementId.NewElementId(e.Id), e.Name, Core.ToList(areas), risk, e.InScope ?? true, string.IsNullOrWhiteSpace(e.Repo) ? FSharpOption<string>.None : FSharpOption<string>.Some(e.Repo)));
        }
        return specs;

        T? Fail<T>(string error) where T : class { errors.Add(error); return null; }
    }

    private static BoundDecision Decision(string id, Dictionary<string, object?> yaml, Dictionary<string, string> byName, List<string> errors)
    {
        if (Str(yaml, "id") != id) errors.Add($"decision {id}: file says id '{Str(yaml, "id")}'");
        if (Str(yaml, "status") != "bound") errors.Add($"decision {id} is listed in the handover but is not bound");
        var binds = new List<ElementId>();
        foreach (var name in List(yaml, "binds").Select(b => b?.ToString()).OfType<string>())
        {
            if (byName.TryGetValue(name, out var el)) binds.Add(ElementId.NewElementId(el));
            else errors.Add($"{id} binds '{name}', which is not an element in the model");
        }
        var checks = new List<DecisionCheck>();
        foreach (var (entry, i) in List(yaml, "checks").Select((c, i) => (c, i)))
        {
            if (entry is null) continue; // an empty entry was skipped before check objects existed; keep that
            var (run, checkId, error) = Check(entry);
            if (run is null) { errors.Add($"decision {id} checks[{i}]: {error}"); continue; }
            if (run.StartsWith("TODO", StringComparison.Ordinal)) continue;
            checks.Add(DecisionCheckModule.create(CheckKind(run), run, checkId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(checkId)));
        }
        return new BoundDecision(DecisionId.NewDecisionId(id), (Str(yaml, "statement") ?? "").Trim(), Core.ToList(binds), Core.ToList(checks));
    }

    /// designc records/1: a check is the command, or {id, run} (run required, id optional, no other keys).
    private static (string? Run, string? Id, string? Error) Check(object? entry)
    {
        switch (entry)
        {
            case string run when run.Trim().Length > 0: return (run.Trim(), null, null);
            case string: return (null, null, "a check needs a command");
            case Dictionary<object, object?> o:
                var extra = o.Keys.Select(k => k.ToString()).Where(k => k is not ("id" or "run")).ToList();
                if (extra.Count > 0) return (null, null, $"a check takes only id and run, not {string.Join(", ", extra)}");
                if (o.GetValueOrDefault("run") is not string command || command.Trim().Length == 0) return (null, null, "a check object needs a run (the command)");
                var checkId = o.GetValueOrDefault("id")?.ToString();
                if (checkId is not null && !CheckIdShape().IsMatch(checkId)) return (null, null, $"check id '{checkId}' must match [A-Za-z0-9._-]+");
                return (command.Trim(), checkId, null);
            default: return (null, null, "a check is a command string or {id, run}");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial System.Text.RegularExpressions.Regex CheckIdShape();

    private static List<PathPattern> Patterns(IEnumerable<string> raw, string where, List<string> errors)
    {
        var list = new List<PathPattern>();
        foreach (var r in raw)
        {
            var parsed = PathPatternModule.create(r);
            if (parsed.IsOk) list.Add(parsed.ResultValue);
            else errors.Add($"{where}: {parsed.ErrorValue}");
        }
        return list;
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();
    private static string? Str(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key)?.ToString();
    private static IEnumerable<object?> List(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) as List<object?> ?? [];

    private static void Reject(string dir, IEnumerable<string> errors) =>
        throw new FactoryRejectedException($"handover {Path.GetFileName(Path.TrimEndingDirectorySeparator(dir))} is malformed", errors.ToArray());
}
