using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Factory.Integration.Tests.Support;
using Factory.Integration.Tests.Verify;
using Factory.S2;
using Xunit;
using Xunit.Abstractions;

namespace Factory.Integration.Tests.Cli;

public class FeaturesOptionsTests
{
    [Fact]
    public void Flags_repeat_for_start_and_param_and_the_rest_have_defaults()
    {
        var o = FeaturesCommand.Parse(["--registry", "r", "--repo", "p", "--designc", "d", "--start", "a b", "--start", "c", "--base-url", "http://x:1",
            "--param", "id=7", "--param", "k=a=b", "--item", "W-1"], null);

        Assert.Equal(["a b", "c"], o.Starts);
        Assert.Equal([("id", "7"), ("k", "a=b")], o.Params);
        Assert.Equal(("all", null, "W-1", "web", 60), (o.Feature, o.Journey, o.Item, o.Adapter, o.StartTimeoutSeconds));
        Assert.Null(o.Out);
    }

    [Fact]
    public void Designc_falls_back_to_the_environment_and_what_is_required_is_named()
    {
        Assert.Equal("/env", FeaturesCommand.Parse(["--registry", "r", "--repo", "p", "--start", "s", "--base-url", "u"], "/env").Designc);
        Assert.Contains("--designc", Assert.Throws<ArgumentException>(() => FeaturesCommand.Parse(["--registry", "r", "--repo", "p", "--start", "s", "--base-url", "u"], null)).Message);
        Assert.Contains("--start", Assert.Throws<ArgumentException>(() => FeaturesCommand.Parse(["--registry", "r", "--repo", "p", "--designc", "d", "--base-url", "u"], null)).Message);
        Assert.Contains("--param", Assert.Throws<ArgumentException>(() => FeaturesCommand.Parse(["--registry", "r", "--repo", "p", "--designc", "d", "--start", "s", "--base-url", "u", "--param", "oops"], null)).Message);
    }
}

/// The command's logic with designc's fixture adapter and a trivial app the command starts itself. Needs a designc checkout
/// (FACTORY_DESIGNC_DIR) with node and python; without one each test returns at once and says so in its output.
public class FeaturesCommandTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("features-cmd").FullName;
    private string Repo => Path.Combine(_root, "app");
    private string Registry => Path.Combine(_root, "registry");
    public void Dispose() => Directory.Delete(_root, true);

    private const string AppJs = """
        const http = require('http');
        const bad = process.env.MODE === 'badshape';
        http.createServer((q, r) => {
          if (q.url.startsWith('/api/exports/')) { r.setHeader('content-type', 'application/json'); r.end(JSON.stringify({ rows: [{ cedant: bad ? 5 : 'Acme' }] })); }
          else r.end('ok');
        }).listen(Number(process.argv[2]), '127.0.0.1');
        """;

    private string? Designc()
    {
        var d = UiPreviewFixture.DesigncDir();
        if (d is null || !File.Exists(Path.Combine(d, "scripts", "feature-run.js")))
        {
            output.WriteLine("SKIPPED: no designc checkout with scripts/feature-run.js (set FACTORY_DESIGNC_DIR); the end-to-end feature runner test did not run.");
            return null;
        }
        return d;
    }

    private async Task SetUpAsync(string designc, bool withEffect = true)
    {
        Directory.CreateDirectory(Repo);
        File.WriteAllText(Path.Combine(Repo, "app.js"), AppJs);
        await UiPreviewFixture.Git(Repo, "init", "-q", "-b", "main");
        await UiPreviewFixture.Git(Repo, "config", "user.name", "factory-test");
        await UiPreviewFixture.Git(Repo, "config", "user.email", "factory-test@localhost");
        await UiPreviewFixture.Git(Repo, "add", "-A");
        await UiPreviewFixture.Git(Repo, "commit", "-qm", "app");
        var fx = Path.Combine(designc, "test", "fixtures", "feature-run");
        Directory.CreateDirectory(_root);
        CopyDir(Path.Combine(fx, "registry"), Registry);
        File.Copy(Path.Combine(fx, "app.json"), Path.Combine(_root, "app.json"));
        if (!withEffect) return;
        var f = Path.Combine(Registry, "features", "orders-export.yaml");
        var yaml = File.ReadAllText(f)
            .Replace("screens: [orders]", "screens: [orders]\npreconditions:\n  data: { id: { param: id } }")
            .Replace("      ui: { state: ready, contains: [{ role: link, name: Download }] }",
                "      ui: { state: ready, contains: [{ role: link, name: Download }] }\n      effect:\n        api: { method: GET, path: /api/exports/:id }\n        expect: { status: 200, shape: { rows: [{ cedant: \"text:Aa\" }] } }");
        File.WriteAllText(f, yaml);
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(string designc, int port, string start, params string[] extra)
    {
        var args = new List<string> { "--registry", Registry, "--repo", Repo, "--designc", designc, "--start", start,
            "--base-url", $"http://127.0.0.1:{port}", "--adapter", $"fixture:{Path.Combine(_root, "app.json")}", "--param", "id=7",
            "--out", Path.Combine(_root, "out") };
        args.AddRange(extra);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await FeaturesCommand.RunAsync(FeaturesCommand.Parse(args.ToArray(), null), stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string[] Rows()
    {
        var f = Path.Combine(Registry, "ledger", "pipeline.jsonl");
        return File.Exists(f) ? File.ReadAllLines(f).Where(l => l.Trim().Length > 0).ToArray() : [];
    }

    [Fact]
    public async Task A_passing_feature_is_recorded_at_the_repos_head_with_the_item_and_the_app_is_stopped()
    {
        if (Designc() is not { } designc) return;
        await SetUpAsync(designc);
        var port = FreePort();

        var r = await RunAsync(designc, port, $"node app.js {port}", "--item", "W-feat");

        Assert.True(r.Exit == 0, r.Out + r.Err);
        var row = JsonDocument.Parse(Assert.Single(Rows())).RootElement;
        Assert.Equal("feature_result", row.GetProperty("kind").GetString());
        Assert.Equal("orders-export", row.GetProperty("feature").GetString());
        Assert.Equal("pass", row.GetProperty("outcome").GetString());
        Assert.Equal("W-feat", row.GetProperty("item").GetString());
        Assert.Equal(40, row.GetProperty("commit").GetString()!.Length);
        Assert.Equal(2, row.GetProperty("journeys").GetArrayLength());
        Assert.Empty(DesigncSchema.Validate(Assert.Single(Rows())));
        Assert.Contains("orders-export: pass (2 runs, 0 failed)", r.Out);
        Assert.True(File.Exists(Path.Combine(_root, "out", "effects", "orders-export--export-all.json")));
        Assert.False(Factory.S2.AppLauncher.IsServing(new Uri($"http://127.0.0.1:{port}")));
    }

    [Fact]
    public async Task A_wrong_effect_shape_fails_every_run_of_the_journey_with_an_effect_failure_naming_no_value()
    {
        if (Designc() is not { } designc) return;
        await SetUpAsync(designc);
        var port = FreePort();

        var r = await RunAsync(designc, port, $"MODE=badshape node app.js {port}");

        Assert.Equal(1, r.Exit);
        var row = JsonDocument.Parse(Assert.Single(Rows())).RootElement;
        Assert.Equal("fail", row.GetProperty("outcome").GetString());
        foreach (var j in row.GetProperty("journeys").EnumerateArray())
        {
            var f = j.GetProperty("failures")[0];
            Assert.Equal("effect", f.GetProperty("kind").GetString());
            Assert.Equal(3, f.GetProperty("step").GetInt32()); // click Export, wait, then the effect
            Assert.Equal($"effect GET /api/exports/7: rows[0].cedant is a number, expected text", f.GetProperty("msg").GetString());
        }
    }

    [Fact]
    public async Task A_busy_port_is_refused_exit_3_with_nothing_recorded()
    {
        if (Designc() is not { } designc) return;
        await SetUpAsync(designc);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var r = await RunAsync(designc, port, $"node app.js {port}");

        Assert.Equal(3, r.Exit);
        Assert.Contains("the Factory drives only an app it started", r.Err);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task An_app_that_does_not_come_up_is_exit_3_with_nothing_recorded()
    {
        if (Designc() is not { } designc) return;
        await SetUpAsync(designc);

        var r = await RunAsync(designc, FreePort(), "exit 1");

        Assert.Equal(3, r.Exit);
        Assert.Contains("the app exited (code 1) before it answered", r.Err);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task An_unknown_feature_or_a_dirty_repo_or_evidence_inside_the_repo_is_exit_2_and_starts_nothing()
    {
        if (Designc() is not { } designc) return;
        await SetUpAsync(designc);
        var port = FreePort();
        var marker = Path.Combine(_root, "started");

        var unknown = await RunAsync(designc, port, $"touch {marker}", "--feature", "nope");
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("nope", unknown.Err);

        var inside = await RunAsync(designc, port, $"touch {marker}", "--out", Path.Combine(Repo, "evidence"));
        Assert.Equal(2, inside.Exit);
        Assert.Contains("inside", inside.Err);

        File.WriteAllText(Path.Combine(Repo, "dirty.txt"), "x");
        var dirty = await RunAsync(designc, port, $"touch {marker}");
        Assert.Equal(2, dirty.Exit);
        Assert.Contains("uncommitted changes", dirty.Err);

        Assert.False(File.Exists(marker));
        Assert.Empty(Rows());
    }
}
