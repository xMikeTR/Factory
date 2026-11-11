using System.Text.Json;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// What S2 asks for per task and phase, what it writes on the task's `context` ledger row, and how the source is chosen.
public class ContextRecordingTests
{
    private static readonly string Example =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "context-bundle.example.json"));
    private static readonly BundleRequest Request = new("20261102-abcd1234", null, "T-1", "implementer", ["Tags/Tags API"]);

    [Fact]
    public void Roles_are_named_for_the_phase_that_holds_the_pen()
    {
        Assert.Equal("test-author", ContextRecording.Role(testAuthor: true));
        Assert.Equal("implementer", ContextRecording.Role(testAuthor: false));
    }

    [Fact]
    public void A_fetched_bundle_is_recorded_with_its_id_and_procedures()
    {
        var req = Request with { Role = "builder" };
        var fetched = ContextBundle.Parse(Example, req, "file");
        using var doc = JsonDocument.Parse(ContextRecording.Payload("builder", fetched));
        var p = doc.RootElement;

        Assert.Equal("builder", p.GetProperty("role").GetString());
        Assert.Equal("file", p.GetProperty("source").GetString());
        Assert.StartsWith("sha256:4a57987a", p.GetProperty("bundle_id").GetString());
        var proc = Assert.Single(p.GetProperty("procedures").EnumerateArray());
        Assert.Equal(("change-ui-list", "2", "30fead3d350b4809"),
            (proc.GetProperty("id").GetString(), proc.GetProperty("version").GetString(), proc.GetProperty("hash").GetString()));
        Assert.False(p.TryGetProperty("missed", out _));
    }

    [Fact]
    public void A_miss_is_recorded_with_its_source_and_reason_and_no_bundle_id()
    {
        using var doc = JsonDocument.Parse(ContextRecording.Payload("implementer", new BundleFetch.Missed("designc", "timed out after 10s")));
        var p = doc.RootElement;

        Assert.Equal("designc", p.GetProperty("source").GetString());
        Assert.Equal("timed out after 10s", p.GetProperty("missed").GetString());
        Assert.False(p.TryGetProperty("bundle_id", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    public void No_setting_means_no_source(string? kind) =>
        Assert.IsType<NoContextSource>(ContextRecording.Source(kind, dir: null, script: null, registry: "/r"));

    [Fact]
    public void File_and_designc_need_their_setting_and_an_unknown_kind_is_refused()
    {
        Assert.IsType<FileContextSource>(ContextRecording.Source("file", dir: "/h", script: null, registry: "/r"));
        Assert.IsType<DesigncCliContextSource>(ContextRecording.Source("designc", dir: null, script: "/d/designc-core.js", registry: "/r"));
        Assert.Throws<ArgumentException>(() => ContextRecording.Source("file", dir: null, script: null, registry: "/r"));
        Assert.Throws<ArgumentException>(() => ContextRecording.Source("designc", dir: null, script: null, registry: "/r"));
        Assert.Throws<ArgumentException>(() => ContextRecording.Source("mcp", dir: null, script: null, registry: "/r"));
    }

    [Fact]
    public void Injection_is_off_unless_asked_for()
    {
        Assert.False(ContextRecording.Inject(null));
        Assert.False(ContextRecording.Inject("0"));
        Assert.True(ContextRecording.Inject("1"));
    }
}
