using Factory.Core;

namespace Factory.Harness.Registry;

/// What System 2 is configured with rather than handed by System 1.
/// ProtectedPaths: paths no task may write (the registry, CI config, verify scripts).
public sealed record S2Config(IReadOnlyList<string> ProtectedPaths)
{
    public static S2Config Default { get; } = new(["registry/**", ".github/**", ".factory/**", "verify/**", "scripts/verify/**",
        "ui-contract/previews/**", "ui-contract/specs/**", "ui-contract/manifests/**", "ui-contract/preview/**", "ui-contract/tests/**", "ui-contract/holdout/**",
        "vitest.config.*", ".claude/hooks/**"]);
}

/// Reads a frozen System 1 handover into the F# contract. Throws FactoryRejectedException with
/// every problem found when the package is malformed, tampered with, or inconsistent.
public interface IHandoverSource
{
    Task<Handover> LoadAsync(string handoverDir, S2Config config, CancellationToken ct);
}
