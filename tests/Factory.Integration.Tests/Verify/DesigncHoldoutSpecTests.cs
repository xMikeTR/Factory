using System.Diagnostics;
using System.Text.Json;
using Factory.Core;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Verify;

/// The real `designc-core.js holdout-spec` against a registry built by designc's own slice-6 test helper (it has one holdout case, AC-UI-1 case 4):
/// the Factory's parser accepts the real JSON and the sha matches designc's own hash of the case. Opt-in: FACTORY_TEST_DESIGNC=1 (needs node and Designc/app/node_modules).
public sealed class DesigncHoldoutSpecTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DESIGNC") == "1";

    private static string DesigncApp()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var app = Path.Combine(d.FullName, "..", "..", "Designc", "app");
            if (d.Name == "factory-s2-workbook" && Directory.Exists(app)) return Path.GetFullPath(app);
        }
        throw new InvalidOperationException("Designc/app not found next to System2/ (two levels above the workbook)");
    }

    // Writes the registry exactly as test/holdout-spec.test.js does, and prints it with designc's own hash of the holdout case.
    private const string Build = """
        const fs = require('fs'), os = require('os'), path = require('path'), yaml = require('js-yaml');
        const Core = require('./renderer/core.js');
        const { fixture } = require('./test/helpers/handover-ui.js');
        const f = fixture(), dir = fs.mkdtempSync(path.join(os.tmpdir(), 'designc-hold-'));
        fs.mkdirSync(path.join(dir, 'model')); fs.mkdirSync(path.join(dir, 'acceptance'));
        fs.writeFileSync(path.join(dir, 'model/workspace.dsl'), f.modelText);
        for (const c of f.criteria) { const doc = { ...c }; for (const k of Object.keys(doc)) if (k.startsWith('_')) delete doc[k]; fs.writeFileSync(path.join(dir, c._path), yaml.dump(doc)); }
        console.log(JSON.stringify({ dir, sha256: Core.holdoutCaseHash(f.criteria[0].cases[4]) }));
        """;

    [Fact]
    public async Task The_real_holdout_spec_output_parses_and_its_sha_is_the_one_designc_froze()
    {
        if (!Enabled) return;
        var app = DesigncApp();
        var psi = new ProcessStartInfo("node") { WorkingDirectory = app, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(Build);
        using var p = Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        using var built = JsonDocument.Parse(json);
        var dir = built.RootElement.GetProperty("dir").GetString()!;
        try
        {
            var (spec, why) = await UiAcceptanceHoldout.GenerateSpecAsync(app, dir, new UiHoldoutCase("AC-UI-1", 4, "ready", ""), default);
            Assert.True(spec is not null, why);
            Assert.Equal("ready", spec!.State);
            Assert.Contains("test(", spec.Text);
            Assert.Equal(built.RootElement.GetProperty("sha256").GetString(), spec.Sha256, ignoreCase: true);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
