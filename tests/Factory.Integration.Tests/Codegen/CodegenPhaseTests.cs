using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Generation;

public sealed class CodegenPhaseTests
{
    private const string Path1 = "contracts/openapi/fare-quotes.yaml";
    private const string Stem = "generated/contracts/csharp/FareQuotes/FareQuotes";

    private static Task<TeamHarness> HarnessAsync(FakeCodeGenerator gen, string yaml = "openapi: 3.0.0\n") =>
        TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: gen,
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, yaml));

    [Fact]
    public async Task A_handover_without_interfaces_logs_none_and_runs_no_generator()
    {
        var gen = new FakeCodeGenerator();
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: gen);
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        Assert.Equal("none", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
        Assert.Equal(0, gen.Calls);
    }

    [Fact]
    public async Task Generation_writes_files_and_a_manifest_and_logs_generated()
    {
        var gen = new FakeCodeGenerator();
        using var h = await HarnessAsync(gen);
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        var row = Assert.Single(h.Rows(LedgerKinds.Codegen));
        Assert.Equal("generated", row.Outcome);
        using var doc = JsonDocument.Parse(row.Payload!);
        var tool = Assert.Single(doc.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("nswag", tool.GetProperty("name").GetString());
        Assert.Equal(Codegen_Version, tool.GetProperty("version").GetString());
        Assert.Equal<IEnumerable<string?>>([$"{Stem}.Dtos.g.cs", $"{Stem}.Server.g.cs"], doc.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
    }

    private static string Codegen_Version => Factory.Core.Codegen.nswagVersion;

    [Fact]
    public async Task Two_runs_on_the_same_handover_give_identical_files_and_changed_text_changes_them()
    {
        async Task<string> RunAsync(string yaml)
        {
            using var h = await HarnessAsync(new FakeCodeGenerator(), yaml);
            await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
            return await File.ReadAllTextAsync(Path.Combine(CodegenPhase.WorkDir(h.Services.Settings.RunDir(TeamHarness.RunId)), CodegenPhase.ManifestFile));
        }
        var a = await RunAsync("openapi: 3.0.0\n");
        Assert.Equal(a, await RunAsync("openapi: 3.0.0\n"));
        Assert.NotEqual(a, await RunAsync("openapi: 3.0.1\n"));
    }

    [Fact]
    public async Task A_failing_generator_ends_the_run_codegen_failed_with_the_reason()
    {
        using var h = await HarnessAsync(new FakeCodeGenerator("boom"));
        var finished = await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        Assert.Equal("codegen_failed", finished?.Status);
        Assert.Contains("boom", finished!.Detail);
        Assert.Equal("failed", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
    }

    [Fact]
    public async Task A_resume_reuses_what_was_generated()
    {
        var gen = new FakeCodeGenerator();
        using var h = await HarnessAsync(gen);
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        Assert.Equal(1, gen.Calls);
        Assert.Equal<IEnumerable<string?>>(["generated", "reused"], h.Rows(LedgerKinds.Codegen).Select(e => e.Outcome));
    }

    [Fact]
    public async Task Off_logs_off_and_generates_nothing()
    {
        var gen = new FakeCodeGenerator();
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: gen,
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, "x"), shapeSettings: s => s with { CodegenOn = false });
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        Assert.Equal("off", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
        Assert.Equal(0, gen.Calls);
    }

    [Fact]
    public async Task A_policy_naming_a_repo_the_run_does_not_have_is_refused()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: new FakeCodeGenerator(),
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, "x"), shapeSettings: s => s with { CodegenCsharp = ["Api"] });
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        Assert.Contains("Api", string.Join("\n", e.Errors));
    }

    [Fact]
    public async Task Seeding_replaces_the_generated_folder_on_the_integration_branch_and_commits_it_once()
    {
        using var h = await HarnessAsync(new FakeCodeGenerator());
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        var stale = Path.Combine(integration, "generated", "contracts", "csharp", "Old.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "// old\n");

        await CodegenPhase.SeedAsync(h.Services, TeamHarness.RunId, default);
        await CodegenPhase.SeedAsync(h.Services, TeamHarness.RunId, default); // a resume does nothing

        Assert.True(File.Exists(Path.Combine(integration, $"{Stem}.Dtos.g.cs")));
        Assert.True(File.Exists(Path.Combine(integration, $"{Stem}.Server.g.cs")));
        Assert.False(File.Exists(stale));
        var log = (await h.GitIn(integration, "log", "--format=%s")).Output;
        Assert.Single(log.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("factory: seed generated contracts"));
        Assert.Empty(CodegenPhase.Drift(h.Services, TeamHarness.RunId));

        File.AppendAllText(Path.Combine(integration, $"{Stem}.Dtos.g.cs"), "// edit\n");
        File.WriteAllText(Path.Combine(integration, "generated", "contracts", "csharp", "Extra.cs"), "x");
        var drift = Assert.Single(CodegenPhase.Drift(h.Services, TeamHarness.RunId)).Drift;
        Assert.Equal([$"added: generated/contracts/csharp/Extra.cs", $"modified: {Stem}.Dtos.g.cs"], drift.ToArray());
    }

    [Fact]
    public void The_policy_file_reads_the_csharp_repo_list_and_refuses_duplicates()
    {
        var dir = Directory.CreateTempSubdirectory("cgpol-").FullName;
        try
        {
            Assert.Null(CodegenPolicyFile.Load(dir).Csharp);
            Directory.CreateDirectory(Path.Combine(dir, ".factory"));
            var file = Path.Combine(dir, MergePolicyFile.RelativePath);
            File.WriteAllText(file, "codegen:\n  csharp: [Api, Worker]\n");
            Assert.Equal(["Api", "Worker"], CodegenPolicyFile.Load(dir).Csharp!.ToArray());
            File.WriteAllText(file, "codegen:\n  csharp: [Api, Api]\n");
            Assert.Throws<FactoryRejectedException>(() => CodegenPolicyFile.Load(dir));
            Assert.Single(CodegenPolicyFile.Unknown(["Api", "Zed"], new HashSet<string> { "Api" }));
            Assert.Single(CodegenPolicyFile.Unknown(["Api"], new HashSet<string>()));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_failed_generation_stops_the_workflow_before_the_planner_is_called()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), codegen: new FakeCodeGenerator("boom"),
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, "x"));
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new Approver(), default);
        Assert.Equal("codegen_failed", result?.Status);
        Assert.Empty(h.Rows(LedgerKinds.Plan));
        Assert.Equal("failed", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
    }

    private const string TsStem = "generated/contracts/ts/FareQuotes/FareQuotes";
    private const string Yaml = """
        openapi: 3.0.0
        paths:
          /quotes:
            get: { operationId: listQuotes, responses: { "200": { description: ok } } }
          /quotes/{id}:
            get: { operationId: getQuote, responses: { "200": { description: ok } } }
        components:
          schemas:
            Quote: { type: object }
            Aaa: { type: object }
        """;

    private static Handover TwoElements(Handover b, string text, params (string Id, string Method, string Route)[] more) =>
        b.WithClientContract(b.Elements[0].Id.Value, b.Elements[1].Id.Value, Path1, text, more);

    private static Task<TeamHarness> TsHarnessAsync(string text = Yaml, bool packageJsonOnBase = false, Func<FactorySettings, FactorySettings>? settings = null, params (string Id, string Method, string Route)[] more) =>
        TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: new FakeCodeGenerator(),
            seedRepo: packageJsonOnBase ? r => File.WriteAllText(System.IO.Path.Combine(r, "package.json"), "{}\n") : null,
            shapeHandover: b => TwoElements(b, text, more), shapeSettings: settings ?? (s => s with { CodegenTypescript = ["."] }));

    private static async Task<(string Manifest, Dictionary<string, string> Files)> GenerateAsync(TeamHarness h)
    {
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        var work = CodegenPhase.WorkDir(h.Services.Settings.RunDir(TeamHarness.RunId));
        var files = Directory.EnumerateFiles(System.IO.Path.Combine(work, "out"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => System.IO.Path.GetRelativePath(work, f), File.ReadAllText);
        return (await File.ReadAllTextAsync(System.IO.Path.Combine(work, CodegenPhase.ManifestFile)), files);
    }

    [Fact]
    public async Task Both_languages_are_generated_one_manifest_v2_lists_both_tools_and_the_sources()
    {
        using var h = await TsHarnessAsync();
        var (manifest, files) = await GenerateAsync(h);
        Assert.Equal(5, files.Count); // C#: Dtos + Server + Client, TS: schema + client
        Assert.Contains(files.Keys, k => k.EndsWith($"{TsStem}.schema.ts"));
        Assert.Contains("createFareQuotesClient", files.Single(f => f.Key.EndsWith(".client.ts")).Value);
        using var doc = JsonDocument.Parse(manifest);
        Assert.Equal<IEnumerable<string?>>(["nswag", "openapi-typescript"], doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(Codegen.openapiTypescriptVersion, doc.RootElement.GetProperty("tools")[1].GetProperty("version").GetString());
        var src = Assert.Single(doc.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Equal(Path1, src.GetProperty("path").GetString());
        Assert.Equal<IEnumerable<string?>>(["/quotes", "/quotes/{id}"], src.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("path").GetString()));
        Assert.Equal<IEnumerable<string?>>(["listQuotes", "getQuote"], src.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.Equal<IEnumerable<string?>>(["Aaa", "Quote"], src.GetProperty("schemas").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(5, doc.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal("generated", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
    }

    [Fact]
    public async Task Two_fresh_runs_give_byte_identical_files_and_manifest_and_a_changed_text_regenerates_both_sides()
    {
        using var h1 = await TsHarnessAsync();
        using var h2 = await TsHarnessAsync();
        using var h3 = await TsHarnessAsync(Yaml + "# changed\n");
        var a = await GenerateAsync(h1);
        var b = await GenerateAsync(h2);
        var c = await GenerateAsync(h3);
        Assert.Equal(a.Manifest, b.Manifest);
        Assert.Equal(a.Files.OrderBy(x => x.Key), b.Files.OrderBy(x => x.Key));
        foreach (var k in a.Files.Keys) Assert.NotEqual(a.Files[k], c.Files[k]); // every C# file, the schema and the client header
    }

    [Fact]
    public async Task A_second_called_operation_adds_a_client_function()
    {
        using var h = await TsHarnessAsync(more: ("getQuote", "GET", "/quotes/{id}"));
        var (_, files) = await GenerateAsync(h);
        var client = files.Single(f => f.Key.EndsWith(".client.ts")).Value;
        Assert.Contains("getQuote: (args", client);
        Assert.Contains("listQuotes: (args", client);
    }

    [Fact]
    public async Task Typescript_only_lists_only_openapi_typescript()
    {
        using var h = await TsHarnessAsync(settings: s => s with { CodegenTypescript = ["."], CodegenCsharp = [] });
        var (manifest, files) = await GenerateAsync(h);
        using var doc = JsonDocument.Parse(manifest);
        Assert.Equal<IEnumerable<string?>>(["openapi-typescript"], doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task The_default_is_decided_by_package_json_on_the_base_branch_not_the_working_tree()
    {
        using var committed = await TsHarnessAsync(packageJsonOnBase: true, settings: s => s);
        Assert.Equal(5, (await GenerateAsync(committed)).Files.Count);

        using var untracked = await TsHarnessAsync(settings: s => s);
        File.WriteAllText(System.IO.Path.Combine(untracked.Services.Settings.RepoPath, "package.json"), "{}\n");
        Assert.Equal(3, (await GenerateAsync(untracked)).Files.Count); // C# only: Dtos + Server + Client
    }

    [Fact]
    public async Task Seeding_commits_csharp_and_ts_in_one_commit_and_an_edit_or_added_file_under_ts_is_drift()
    {
        using var h = await TsHarnessAsync();
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        await CodegenPhase.SeedAsync(h.Services, TeamHarness.RunId, default);
        Assert.True(File.Exists(System.IO.Path.Combine(integration, $"{TsStem}.schema.ts")));
        Assert.True(File.Exists(System.IO.Path.Combine(integration, $"{TsStem}.client.ts")));
        Assert.True(File.Exists(System.IO.Path.Combine(integration, $"{Stem}.Dtos.g.cs")));
        var files = (await h.GitIn(integration, "show", "--name-only", "--format=%s", "HEAD")).Output;
        Assert.Contains("factory: seed generated contracts", files);
        Assert.Contains($"{TsStem}.schema.ts", files);
        Assert.Contains($"{Stem}.Dtos.g.cs", files);
        Assert.Empty(CodegenPhase.Drift(h.Services, TeamHarness.RunId));

        File.AppendAllText(System.IO.Path.Combine(integration, $"{TsStem}.client.ts"), "// edit\n");
        File.WriteAllText(System.IO.Path.Combine(integration, "generated", "contracts", "ts", "extra.ts"), "x");
        var drift = Assert.Single(CodegenPhase.Drift(h.Services, TeamHarness.RunId)).Drift;
        Assert.Equal(["added: generated/contracts/ts/extra.ts", $"modified: {TsStem}.client.ts"], drift.ToArray());
    }

    [Fact]
    public async Task A_typescript_policy_naming_an_unknown_repo_is_refused_by_its_key()
    {
        using var h = await TsHarnessAsync(settings: s => s with { CodegenTypescript = ["Ui"] });
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        Assert.Contains("codegen.typescript", string.Join("\n", e.Errors));
    }

    [Fact]
    public void The_policy_file_reads_typescript_and_handWritten()
    {
        var dir = Directory.CreateTempSubdirectory("cgpol-").FullName;
        try
        {
            Directory.CreateDirectory(System.IO.Path.Combine(dir, ".factory"));
            var file = System.IO.Path.Combine(dir, MergePolicyFile.RelativePath);
            Assert.Equal(QualityMode.Report, CodegenPolicyFile.Load(dir).HandWritten);
            File.WriteAllText(file, "codegen:\n  csharp: [Api]\n  typescript: [Ui, Web]\n  handWritten: block\n");
            var p = CodegenPolicyFile.Load(dir);
            Assert.Equal(["Ui", "Web"], p.Typescript!.ToArray());
            Assert.Equal(["Api"], p.Csharp!.ToArray());
            Assert.Equal(QualityMode.Block, p.HandWritten);
            File.WriteAllText(file, "codegen:\n  handWritten: off\n");
            Assert.Equal(QualityMode.Off, CodegenPolicyFile.Load(dir).HandWritten);
            foreach (var bad in new[] { "typescript: [Ui, Ui]", "typescript: [\"\"]", "handWritten: sometimes" })
            {
                File.WriteAllText(file, "codegen:\n  " + bad + "\n");
                var e = Assert.Throws<FactoryRejectedException>(() => CodegenPolicyFile.Load(dir));
                Assert.Contains(bad.StartsWith("handW") ? "codegen.handWritten" : "codegen.typescript", string.Join("\n", e.Errors));
            }
            Assert.Contains("codegen.typescript", Assert.Single(CodegenPolicyFile.Unknown(["Zed"], new HashSet<string> { "Api" }, "codegen.typescript")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OpenApiIndex_reads_yaml_and_json_and_tolerates_non_documents()
    {
        var y = OpenApiIndex.Read(Yaml);
        Assert.Equal([("listQuotes", "GET", "/quotes"), ("getQuote", "GET", "/quotes/{id}")], y.Operations.Select(o => (o.Id!, o.Method, o.Path)).ToArray());
        Assert.Equal(["Aaa", "Quote"], y.Schemas);
        var j = OpenApiIndex.Read("{\"paths\":{\"/b\":{\"post\":{}, \"get\":{\"operationId\":\"g\"}},\"/a\":{\"delete\":{}}},\"components\":{\"schemas\":{\"Z\":{},\"A\":{}}}}");
        Assert.Equal([("/a", "DELETE", (string?)null), ("/b", "GET", "g"), ("/b", "POST", null)], j.Operations.Select(o => (o.Path, o.Method, o.Id)).ToArray());
        Assert.Equal(["A", "Z"], j.Schemas);
        foreach (var junk in new[] { "x", "openapi: 3.0.0\n", "[1, 2]", ": : :\n\t- {", "" })
        {
            var i = OpenApiIndex.Read(junk);
            Assert.Empty(i.Operations);
            Assert.Empty(i.Schemas);
        }
    }

    private sealed class Approver : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "t"));
    }
}
