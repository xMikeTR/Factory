using Factory.Harness.Registry;

namespace Factory.S2;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// What `run --handover` accepts: a frozen Designc handover directory (read, hash-checked and
/// mapped by DesigncHandoverReader) or an already-mapped handover JSON file.
public static class HandoverInput
{
    /// Returns the path of the handover JSON a run starts from. A directory is frozen to
    /// {stateDir}/handovers/{dirname}.json; a file is returned unchanged.
    public static async Task<string> ResolveAsync(string handoverPath, string stateDir, CancellationToken ct)
    {
        if (!Directory.Exists(handoverPath)) return handoverPath;

        var handover = await new DesigncHandoverReader().LoadAsync(handoverPath, S2Config.Default, ct);
        var dir = Path.Combine(stateDir, "handovers");
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoverPath))) + ".json");
        await File.WriteAllTextAsync(json, Core.HandoverJson(handover), ct);
        return json;
    }
}
