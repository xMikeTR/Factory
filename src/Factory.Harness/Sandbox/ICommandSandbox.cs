namespace Factory.Harness.Sandbox;

/// Where agent and verify commands run. The child sees only the env it is given plus a small
/// allow-list (PATH, HOME, toolchain dirs): never the gateway key, gh token or Langfuse keys.
public interface ICommandSandbox : IAsyncDisposable
{
    /// "docker" | "local"; ledgered with every verify run.
    string Kind { get; }

    Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout,
        IReadOnlyDictionary<string, string> env, CancellationToken ct);
}

public interface ISandboxFactory
{
    Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct);
}
