using Factory.Harness.Policy;

namespace Factory.Harness.Sandbox;

/// The sibling services of .factory/qa-env.yaml, running on a private docker network, and the sandboxes that live on it. The
/// network is internal (no route out) and every container is removed with it. `Env` tells tests where each service is:
/// QA_<NAME>_HOST is the service's name on that network.
public sealed class QaEnvironment(ISandboxFactory sandboxes, IReadOnlyDictionary<string, string> env, Func<Task>? stop = null) : IAsyncDisposable
{
    public ISandboxFactory Sandboxes { get; } = sandboxes;
    public IReadOnlyDictionary<string, string> Env { get; } = env;

    public static string EnvName(string service) =>
        "QA_" + new string(service.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()) + "_HOST";

    public static async Task<QaEnvironment> StartAsync(QaEnv qa, DockerSandboxOptions options, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var network = $"s2-qa-{id}";
        var containers = new List<string>();
        await DockerSandbox.DockerAsync(ct, "network", "create", "--internal", network);
        async Task Stop()
        {
            foreach (var c in containers) await DockerSandbox.DockerAsync(CancellationToken.None, check: false, "rm", "-f", c);
            await DockerSandbox.DockerAsync(CancellationToken.None, check: false, "network", "rm", network);
        }
        try
        {
            var env = new Dictionary<string, string>();
            foreach (var s in qa.Services)
            {
                var name = $"s2-qa-{id}-{s.Name}".ToLowerInvariant();
                await DockerSandbox.DockerAsync(ct, "run", "-d", "--rm", "--name", name, "--network", network, "--network-alias", s.Name,
                    "--memory", "1g", "--pids-limit", "512", "--security-opt", "no-new-privileges", "--cap-drop", "ALL", s.Image);
                containers.Add(name);
                env[EnvName(s.Name)] = s.Name;
            }
            return new QaEnvironment(new DockerSandboxFactory(options with { Network = network }), env, Stop);
        }
        catch
        {
            await Stop();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (stop is not null) await stop();
    }
}
