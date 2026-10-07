using Factory.Harness.Registry;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

using Core = Factory.Core.Core;

public class HandoverInputTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("state-").FullName;
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover");

    [Fact]
    public async Task A_Designc_directory_is_frozen_to_json_in_the_state_dir()
    {
        var path = await HandoverInput.ResolveAsync(Fixture, _state, default);

        Assert.StartsWith(_state, path);
        var expected = await new DesigncHandoverReader().LoadAsync(Fixture, S2Config.Default, default);
        Assert.Equal(expected, Core.ParseHandover(await File.ReadAllTextAsync(path)));
    }

    [Fact]
    public async Task A_json_file_is_used_as_is()
    {
        var json = Path.Combine(_state, "h.json");
        await File.WriteAllTextAsync(json, "{}");
        Assert.Equal(json, await HandoverInput.ResolveAsync(json, _state, default));
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
