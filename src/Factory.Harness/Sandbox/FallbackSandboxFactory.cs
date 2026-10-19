namespace Factory.Harness.Sandbox;

/// FACTORY_SANDBOX=hub,docker,local: tries each sandbox in order and uses the first that starts. A failure is
/// reported through `onSkipped` (the run ledgers it) and the next is tried; a cancelled start is not a failure.
/// Which names are listed is the person's choice: `local` runs unisolated, so it is never added on its own.
public sealed class FallbackSandboxFactory(IReadOnlyList<(string Name, ISandboxFactory Factory)> candidates, Action<string, string> onSkipped) : ISandboxFactory
{
    public static readonly IReadOnlyList<string> Known = ["hub", "docker", "local"];

    /// The ordered names from the variable; unset means docker only.
    public static IReadOnlyList<string> Parse(string? value)
    {
        var names = (value ?? "docker").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) throw new ArgumentException("FACTORY_SANDBOX: no sandbox named");
        foreach (var n in names)
            if (!Known.Contains(n)) throw new ArgumentException($"FACTORY_SANDBOX: '{n}' is not {string.Join('|', Known)}");
        if (names.Distinct().Count() != names.Length) throw new ArgumentException("FACTORY_SANDBOX: a sandbox is named twice");
        return names;
    }

    public async Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct)
    {
        var reasons = new List<string>();
        foreach (var (name, factory) in candidates)
        {
            try { return await factory.ForTeamAsync(teamId, worktree, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                reasons.Add($"{name}: {e.Message}");
                onSkipped(name, e.Message);
            }
        }
        throw new InvalidOperationException("No sandbox could start. " + string.Join("; ", reasons));
    }
}

/// The agentweave hub as a sandbox (the hub is the sandbox). Not wired yet: its API is not documented here, so
/// asking for it fails with a reason and the next sandbox in FACTORY_SANDBOX takes over.
public sealed class HubSandboxFactory : ISandboxFactory
{
    public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) =>
        throw new NotSupportedException("the hub adapter is not implemented yet");
}
