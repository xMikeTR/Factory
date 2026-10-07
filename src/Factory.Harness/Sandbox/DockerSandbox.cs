namespace Factory.Harness.Sandbox;

/// Image built from docker/runner. NuGetCache is a host directory (owned by the host user)
/// shared by every team container, so restores after the first are local.
public sealed record DockerSandboxOptions(
    string Image = "factory-s2-runner:latest",
    string Memory = "4g",
    double Cpus = 2,
    int Pids = 1024,
    string? NuGetCache = null,
    bool RestoreOnStart = true)
{
    public string NuGetCacheDir => NuGetCache ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "factory-s2", "nuget");
}

/// One container per team: the worktree bind-mounted at its own host path (so paths in
/// commands and output mean the same thing inside and out), the host uid/gid, resource limits,
/// and --network none. The network is attached only for the restore window at start.
/// Nothing from the host environment crosses: docker exec gets the given env and a fixed few.
public sealed class DockerSandbox : ICommandSandbox
{
    private static readonly TimeSpan DockerTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(10);
    private const string RestoreNetwork = "bridge";

    private readonly string _container;
    private readonly string _worktree;
    private readonly string _nuget;

    private DockerSandbox(string container, string worktree, string nuget)
    {
        _container = container;
        _worktree = worktree;
        _nuget = nuget;
    }

    public string Kind => "docker";
    public string Container => _container;

    /// The restore window's result; null when there was nothing to restore.
    public ProcessResult? Restore { get; private set; }

    public static async Task<DockerSandbox> StartAsync(string teamId, string worktree, DockerSandboxOptions o, CancellationToken ct)
    {
        worktree = Path.GetFullPath(worktree);
        Directory.CreateDirectory(o.NuGetCacheDir);
        var team = Sanitize(teamId);
        var name = $"s2-{team[..Math.Min(24, team.Length)]}-{Guid.NewGuid():N}";
        var user = $"{await IdAsync("-u", ct)}:{await IdAsync("-g", ct)}";
        await DockerAsync(ct, "run", "-d", "--rm", "--name", name,
            "--network", "none", "--memory", o.Memory, "--cpus", o.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--pids-limit", o.Pids.ToString(), "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
            "--user", user, "--tmpfs", "/tmp:exec,mode=1777",
            "-v", $"{worktree}:{worktree}", "-v", $"{o.NuGetCacheDir}:/nuget",
            "-w", worktree, o.Image, "sleep", "infinity");
        var sandbox = new DockerSandbox(name, worktree, "/nuget");
        try
        {
            if (o.RestoreOnStart && HasProject(worktree)) sandbox.Restore = await sandbox.RestoreWindowAsync(ct);
            return sandbox;
        }
        catch
        {
            await sandbox.DisposeAsync();
            throw;
        }
    }

    public async Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout,
        IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var dir = Path.GetFullPath(workdir);
        if (dir != _worktree && !dir.StartsWith(_worktree + Path.DirectorySeparatorChar))
            throw new ArgumentException($"workdir {dir} is outside the sandbox's worktree {_worktree}", nameof(workdir));

        var args = new List<string> { "exec", "-w", dir };
        foreach (var (k, v) in BaseEnv().Concat(env)) { args.Add("-e"); args.Add($"{k}={v}"); }
        // Kill inside the container: killing the docker CLI alone would leave the command running.
        var secs = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        args.AddRange([_container, "timeout", "-k", "5", secs.ToString(), "bash", "-c", command]);

        var r = await ProcessRunner.RunAsync("docker", args, dir, timeout + TimeSpan.FromSeconds(15), ct: ct, inheritEnvironment: false,
            env: DockerCliEnv());
        var timedOut = r.TimedOut || ((r.ExitCode == 124 || r.ExitCode == 137) && r.Duration >= timeout);
        return timedOut ? r with { ExitCode = 124, TimedOut = true } : r;
    }

    private async Task<ProcessResult> RestoreWindowAsync(CancellationToken ct)
    {
        // "none" is exclusive: swap it for the bridge for the restore, then swap back.
        await DockerAsync(ct, "network", "disconnect", "none", _container);
        try
        {
            await DockerAsync(ct, "network", "connect", RestoreNetwork, _container);
            return await RunAsync("dotnet restore", _worktree, RestoreTimeout, new Dictionary<string, string>(), ct);
        }
        finally
        {
            await DockerAsync(CancellationToken.None, check: false, "network", "disconnect", "--force", RestoreNetwork, _container);
            await DockerAsync(CancellationToken.None, "network", "connect", "none", _container);
        }
    }

    private IEnumerable<KeyValuePair<string, string>> BaseEnv() =>
    [
        new("HOME", "/tmp/home"), new("DOTNET_CLI_HOME", "/tmp/home"), new("NUGET_PACKAGES", _nuget),
        new("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), new("DOTNET_NOLOGO", "1"),
    ];

    public async ValueTask DisposeAsync()
    {
        try { await DockerAsync(CancellationToken.None, "rm", "-f", _container); } catch (InvalidOperationException) { }
    }

    private static bool HasProject(string dir) =>
        Directory.EnumerateFiles(dir, "*.sln").Any() || Directory.EnumerateFiles(dir, "*.*proj").Any();

    private static string Sanitize(string s) => new(s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    private static async Task<string> IdAsync(string flag, CancellationToken ct) =>
        (await ProcessRunner.RunAsync("id", [flag], Environment.CurrentDirectory, DockerTimeout, ct: ct)).Output.Trim();

    /// The docker CLI needs only these to find the engine; secrets stay out of its env too.
    private static Dictionary<string, string> DockerCliEnv()
    {
        var env = new Dictionary<string, string>();
        foreach (var name in new[] { "PATH", "HOME", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG" })
            if (Environment.GetEnvironmentVariable(name) is { } v) env[name] = v;
        return env;
    }

    private static Task DockerAsync(CancellationToken ct, params string[] args) => DockerAsync(ct, check: true, args);

    private static async Task DockerAsync(CancellationToken ct, bool check, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("docker", args, Environment.CurrentDirectory, DockerTimeout, DockerCliEnv(), ct, inheritEnvironment: false);
        if (check && !r.Succeeded) throw new InvalidOperationException($"docker {string.Join(' ', args.Take(3))} failed:\n{r.Tail()}");
    }
}

/// With a ledger, each restore window is ledgered as a verify row at stage "restore": the one
/// time a team container had network.
public sealed class DockerSandboxFactory(DockerSandboxOptions options, ILedger? ledger = null, string? runId = null) : ISandboxFactory
{
    public async Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct)
    {
        var sandbox = await DockerSandbox.StartAsync(teamId, worktree, options, ct);
        if (ledger is not null && runId is not null && sandbox.Restore is { } r)
            await ledger.AppendAsync(new LedgerEvent(runId, "S2", "restore", LedgerKinds.Verify, TeamId: teamId,
                Outcome: r.Succeeded ? "passed" : "failed",
                Payload: System.Text.Json.JsonSerializer.Serialize(new
                {
                    command = "dotnet restore", ExitCode = r.ExitCode, ms = (long)r.Duration.TotalMilliseconds, sandbox = sandbox.Kind,
                    network = "bridge", tail = r.Tail(2000),
                })), ct);
        return sandbox;
    }
}
