using System.Text.RegularExpressions;
using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

public sealed record QaService(string Name, string Image);

/// The black-box QA environment: sibling services started by the harness (isolated network) and what they
/// cannot reproduce. Protected like the policy: read from the base checkout, never a task worktree.
///
///   services:
///     - name: db
///       image: mcr.microsoft.com/mssql/server@sha256:...   # digest-pinned, never a tag
///   fidelity:
///     - Service Bus emulator has no topic filters          # gaps QA cannot judge
public sealed record QaEnv(IReadOnlyList<QaService> Services, IReadOnlyList<string> Fidelity);

public static partial class QaEnvFile
{
    public const string RelativePath = ".factory/qa-env.yaml";

    private sealed class Doc { public List<Svc>? Services { get; set; } public List<string>? Fidelity { get; set; } }
    private sealed class Svc { public string? Name { get; set; } public string? Image { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// Null when the repo has no QA environment (QA then has nothing to run against).
    public static QaEnv? Load(string repoPath)
    {
        var path = Path.Combine(repoPath, RelativePath);
        if (!File.Exists(path)) return null;
        var doc = Yaml.Deserialize<Doc?>(File.ReadAllText(path)) ?? new Doc();
        var errors = new List<string>();
        var services = new List<QaService>();
        foreach (var s in doc.Services ?? [])
        {
            if (s.Name is null || !Name().IsMatch(s.Name)) errors.Add($"services: '{s.Name}' is not a usable service name (letters, digits, . _ - only)");
            else if (s.Image is null || !Digest().IsMatch(s.Image)) errors.Add($"services.{s.Name}: image '{s.Image}' must be pinned by digest (name@sha256:<64 hex>)");
            else services.Add(new QaService(s.Name, s.Image));
        }
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new QaEnv(services, doc.Fidelity ?? []);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9./_:-]*@sha256:[0-9a-f]{64}$")]
    private static partial Regex Digest();
}
