namespace Factory.Harness.Sandbox;

/// Fallback sandbox: runs on the host with a scrubbed environment. No filesystem or network
/// isolation; use DockerSandbox where Docker Engine is available.
public sealed class LocalSandbox : ICommandSandbox
{
    /// Host variables the toolchain needs. Everything else, including every FACTORY_* and
    /// *_KEY / *_TOKEN variable, is dropped.
    public static readonly IReadOnlyList<string> PassThrough =
    [
        "PATH", "HOME", "USER", "LANG", "LC_ALL", "TERM", "TMPDIR", "SHELL",
        "DOTNET_ROOT", "DOTNET_CLI_HOME", "DOTNET_ROLL_FORWARD", "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO", "NUGET_PACKAGES", "JAVA_HOME",
    ];

    public string Kind => "local";

    public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout,
        IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var scrubbed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PassThrough)
            if (Environment.GetEnvironmentVariable(name) is { } v) scrubbed[name] = v;
        foreach (var (k, v) in env) scrubbed[k] = v;
        return ProcessRunner.ShellAsync(command, workdir, timeout, scrubbed, ct, inheritEnvironment: false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LocalSandboxFactory : ISandboxFactory
{
    public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) =>
        Task.FromResult<ICommandSandbox>(new LocalSandbox());
}
