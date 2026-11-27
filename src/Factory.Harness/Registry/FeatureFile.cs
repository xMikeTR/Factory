using System.Text.Json;

namespace Factory.Harness.Registry;

/// One `features/<id>.yaml` of the registry (designc's feature map; designc owns its rules), with only what the Factory's features
/// runner and briefs need. `Id` is the file's base name, which is what `done_when: - feature:` and the runner use.
public sealed record FeatureFile(string Id, string Path, IReadOnlyList<FeatureJourney> Journeys, IReadOnlyList<string> EntryPoints);

/// `StepCount` counts every `steps` entry, including the bare `entry` marker.
public sealed record FeatureJourney(string Id, int StepCount, bool HasEntryMarker, FeatureEffect? Effect);

/// A journey's `proves.effect.api` (method and path) with its `expect` status and shape; the shape is the YAML converted to JSON.
public sealed record FeatureEffect(string Method, string Path, int? Status, JsonElement? Shape);
