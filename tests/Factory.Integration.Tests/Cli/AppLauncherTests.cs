using System.Net;
using System.Net.Sockets;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Cli;

/// Real processes: the launcher's rule is that the Factory drives only an app it started, and leaves nothing running.
public class AppLauncherTests : IDisposable
{
    private readonly string _logs = Directory.CreateTempSubdirectory("launcher-logs").FullName;
    public void Dispose() => Directory.Delete(_logs, true);

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string Server(int port) =>
        $"node -e \"require('http').createServer((q,r)=>r.end('hi')).listen({port},'127.0.0.1')\"";

    private AppLauncher Launcher(string[] commands, int port, double timeoutSeconds = 20) =>
        new(commands, Path.GetTempPath(), $"http://127.0.0.1:{port}", _logs, TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task A_busy_port_is_refused_before_anything_starts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var marker = Path.Combine(_logs, "started");
        await using var launcher = Launcher([$"touch '{marker}'"], port);

        var r = await launcher.StartAsync(CancellationToken.None);

        Assert.False(r.Up);
        Assert.Equal($"something is already serving http://127.0.0.1:{port}; the Factory drives only an app it started. Stop it, or use another port.", r.Reason);
        Assert.False(File.Exists(marker));
        Assert.True(r.Refused);
    }

    [Fact]
    public async Task An_app_comes_up_and_is_stopped_with_its_port_free_afterwards()
    {
        var port = FreePort();
        var launcher = Launcher([Server(port)], port);
        try
        {
            var r = await launcher.StartAsync(CancellationToken.None);
            Assert.True(r.Up, r.Reason);
            Assert.True(AppLauncher.IsServing(new Uri($"http://127.0.0.1:{port}")));
        }
        finally { await launcher.StopAsync(); }

        Assert.False(AppLauncher.IsServing(new Uri($"http://127.0.0.1:{port}")));
        Assert.True(File.Exists(Path.Combine(_logs, "app-1.log")));
    }

    [Fact]
    public async Task Stopping_also_stops_a_wrapped_app_and_is_safe_to_repeat()
    {
        var port = FreePort();
        var launcher = Launcher([$"sh -c \"{Server(port).Replace("\"", "\\\"")}\""], port);
        var r = await launcher.StartAsync(CancellationToken.None);
        Assert.True(r.Up, r.Reason);

        await launcher.StopAsync();
        await launcher.StopAsync();

        Assert.False(AppLauncher.IsServing(new Uri($"http://127.0.0.1:{port}")));
    }

    [Fact]
    public async Task A_command_that_exits_at_once_is_not_up()
    {
        var port = FreePort();
        await using var launcher = Launcher(["exit 3"], port);

        var r = await launcher.StartAsync(CancellationToken.None);

        Assert.False(r.Up);
        Assert.Equal($"the app exited (code 3) before it answered at http://127.0.0.1:{port} (see {Path.Combine(_logs, "app-1.log")})", r.Reason);
        Assert.False(r.Refused);
    }

    [Fact]
    public async Task An_exit_code_is_named()
    {
        var port = FreePort();
        await using var launcher = Launcher(["exit 7"], port);

        var r = await launcher.StartAsync(CancellationToken.None);

        Assert.False(r.Up);
        Assert.Contains("exited (code 7)", r.Reason);
        Assert.Contains(Path.Combine(_logs, "app-1.log"), r.Reason);
    }

    [Fact]
    public async Task The_log_named_is_the_one_of_the_process_that_exited()
    {
        var port = FreePort();
        var launcher = Launcher(["sleep 30", "exit 5"], port);
        try
        {
            var r = await launcher.StartAsync(CancellationToken.None);
            Assert.False(r.Up);
            Assert.Contains("exited (code 5)", r.Reason);
            Assert.Contains(Path.Combine(_logs, "app-2.log"), r.Reason);
        }
        finally { await launcher.StopAsync(); }
    }

    [Fact]
    public async Task A_command_that_never_serves_times_out_and_is_stopped()
    {
        var port = FreePort();
        var launcher = Launcher(["sleep 30"], port, timeoutSeconds: 1);
        try
        {
            var r = await launcher.StartAsync(CancellationToken.None);
            Assert.False(r.Up);
            Assert.Contains("within 1s", r.Reason);
        }
        finally { await launcher.StopAsync(); }
    }

    [Fact]
    public async Task The_commands_output_is_kept_in_a_log_per_command()
    {
        var port = FreePort();
        await using var launcher = Launcher(["echo first-line; exit 1"], port);

        await launcher.StartAsync(CancellationToken.None);

        Assert.Contains("first-line", await File.ReadAllTextAsync(Path.Combine(_logs, "app-1.log")));
    }
}
