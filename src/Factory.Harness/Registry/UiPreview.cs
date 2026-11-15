using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Harness.Registry;

public enum UiPreviewOutcome { Rendered, CouldNotRun, Error }

/// The pure parts of `factory-s2 preview` (docs/ui-contract-verify.md §1): which files a render must produce, the
/// generated test, the command, how to read the runner's answer, and the `ui_preview_rendered` row. Nothing here
/// touches disk, git or a sandbox.
public static partial class UiPreview
{
    public const string Browser = "chromium";
    public const string Platform = "linux";
    public const string DefaultCommand = "npx vitest run --project ui-preview -u --reporter=json {screen}.preview";

    /// A screen names a folder and a test file, so it is held to the spec schema's own id shape.
    public static bool IsScreenName(string screen) => ScreenName().IsMatch(screen);

    /// Paths a complete render holds, relative to `ui-contract/previews/<screen>/`, forward slashes, sorted:
    /// per state a page picture and an ARIA tree, and per placement with an `id` a picture of its own in every state.
    public static IReadOnlyList<string> ExpectedFiles(string specJson)
    {
        using var doc = JsonDocument.Parse(specJson);
        var states = doc.RootElement.TryGetProperty("states", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.EnumerateObject().Select(p => p.Name).ToList() : [];
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Object)
            foreach (var slot in slots.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                foreach (var placement in slot.Value.EnumerateArray())
                    if (placement.ValueKind == JsonValueKind.Object && placement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } v)
                        ids.Add(v);
        var files = new List<string>();
        foreach (var state in states)
        {
            files.Add($"{state}-{Browser}-{Platform}.png");
            files.Add($"{state}.aria.yml");
            foreach (var id in ids) files.Add($"placements/{state}__{id}-{Browser}-{Platform}.png");
        }
        return files.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// The kit's preview test (`bordereaux.preview.test.tsx`), importing the app's own registry instead of the mock library.
    /// Lives in `ui-contract/tests/`, next to `ui-contract/specs/`, `manifests/` and `previews/`.
    public static string PreviewTest(string screen) => $$"""
        import spec from "../specs/{{screen}}.json";
        import { components, layouts } from "../registry";
        import { previewTests } from "../preview/vitest-ui";

        const manifests = Object.values(import.meta.glob("../manifests/*.json", { eager: true, import: "default" })) as any[];

        previewTests(spec as any, { components, layouts, manifests, previewsRel: "../previews" });

        """;

    /// FACTORY_UI_PREVIEW_CMD, or the default; `{screen}` in either is the screen name.
    public static string Command(string screen, string? configured) =>
        (string.IsNullOrWhiteSpace(configured) ? DefaultCommand : configured).Replace("{screen}", screen);

    public static IReadOnlyList<string> CheckArguments(string registry) => ["-m", "designc", "check", registry, "--json", "--no-record"];

    /// Reasons designc's check refuses this screen: every `kind: ui` clause that names the screen, or names no screen
    /// (a pack-level problem). Output that is not designc's JSON is itself a refusal: nothing was judged.
    public static IReadOnlyList<string> SpecRefusals(string checkJson, string screen)
    {
        try
        {
            var start = checkJson.IndexOf('{');
            var end = checkJson.LastIndexOf('}');
            using var doc = JsonDocument.Parse(start >= 0 && end > start ? checkJson[start..(end + 1)] : checkJson);
            var refusals = new List<string>();
            if (doc.RootElement.TryGetProperty("gate", out var gate) && gate.TryGetProperty("clauses", out var clauses))
                foreach (var c in clauses.EnumerateArray())
                {
                    if (c.GetProperty("kind").GetString() != "ui") continue;
                    var named = c.TryGetProperty("screen", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null;
                    var path = c.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    if (named == screen || (named is null && path is null)) refusals.Add(c.GetProperty("text").GetString() ?? "");
                }
            return refusals;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [$"designc check did not answer with JSON, so the spec was not judged: {e.Message}"];
        }
    }

    /// Vitest's JSON reporter, read from the run's output (config noise may surround it). Exit 0 with passing tests is
    /// Rendered (the file check still has to pass). A test that failed is CouldNotRun: a state never rendered, or a
    /// component is not in the app's registry. No tests, no JSON, or a timeout is an Error.
    public static UiPreviewOutcome Interpret(int exitCode, bool timedOut, string output)
    {
        if (timedOut) return UiPreviewOutcome.Error;
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return UiPreviewOutcome.Error;
        try
        {
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            var r = doc.RootElement;
            var failed = r.TryGetProperty("numFailedTests", out var f) ? f.GetInt32() : -1;
            var passed = r.TryGetProperty("numPassedTests", out var p) ? p.GetInt32() : -1;
            if (failed < 0 || passed < 0) return UiPreviewOutcome.Error;
            if (failed > 0) return UiPreviewOutcome.CouldNotRun;
            return exitCode == 0 && passed > 0 ? UiPreviewOutcome.Rendered : UiPreviewOutcome.Error;
        }
        catch (JsonException) { return UiPreviewOutcome.Error; }
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// `ui_preview_rendered {screen, spec_hash, files, image}` (designc's `docs/ui-preview-rendered.example.json`), written the
    /// way BugIntake writes designc rows. `files` is keyed by path relative to `ui/previews/<screen>/`.
    public static RegistryEvent Row(string screen, string specHash, IReadOnlyDictionary<string, string> files, string image, DateTimeOffset now)
    {
        string Body(string? eventId)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("kind", "ui_preview_rendered");
                w.WriteString("by", RegistryLedgerProjector.By);
                w.WriteString("system", "S2");
                w.WriteString("screen", screen);
                w.WriteString("spec_hash", specHash);
                w.WriteStartObject("files");
                foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal)) w.WriteString(path, hash);
                w.WriteEndObject();
                w.WriteString("image", image);
                w.WriteNumber("schema_version", 1);
                w.WriteNumber("ts", now.ToUnixTimeMilliseconds());
                if (eventId is not null) w.WriteString("event_id", eventId);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        var id = "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Body(null))))[..32];
        return new RegistryEvent("designc.jsonl", id, Body(id));
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex ScreenName();
}
