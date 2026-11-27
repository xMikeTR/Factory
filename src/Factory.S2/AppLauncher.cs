using System.Diagnostics;
using System.Net.Sockets;

namespace Factory.S2;

/// `Up` is any HTTP answer at the base URL. `Refused` marks the one case where nothing was started because something already served the URL.
public sealed record LaunchResult(bool Up, string? Reason, bool Refused = false);

/// Starts the app a features run drives, and is the one place that enforces "the Factory never drives an instance it did not start":
/// a base URL something already answers on is refused before any command runs. Plain System.Diagnostics.Process, working directory
/// the repo, output in `<logDir>/app-<n>.log`. StopAsync kills every started process tree and waits for it; call it in a `finally`.
public sealed class AppLauncher(IReadOnlyList<string> commands, string workDir, string baseUrl, string logDir, TimeSpan timeout) : IAsyncDisposable
{
    private readonly List<Process> _started = [];

    /// A TCP connect to the URL's host and port succeeds.
    public static bool IsServing(Uri url)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(url.Host, url.Port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException or ObjectDisposedException) { return false; }
    }

    public async Task<LaunchResult> StartAsync(CancellationToken ct)
    {
        var url = new Uri(baseUrl);
        if (IsServing(url))
            return new(false, $"something is already serving {baseUrl}; the Factory drives only an app it started. Stop it, or use another port.", Refused: true);

        Directory.CreateDirectory(logDir);
        for (var i = 0; i < commands.Count; i++)
        {
            // The command runs in `sh` with stdout and stderr appended to its log; the log path and command travel as arguments, never spliced into the script.
            var psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = workDir, UseShellExecute = false };
            foreach (var a in new[] { "-c", "exec >>\"$1\" 2>&1; eval \"$2\"", "sh", LogPath(i), commands[i] }) psi.ArgumentList.Add(a);
            _started.Add(Process.Start(psi) ?? throw new InvalidOperationException($"could not start: {commands[i]}"));
        }

        var notUp = $"the app did not come up at {baseUrl} within {(int)Math.Ceiling(timeout.TotalSeconds)}s (see {LogPath(0)})";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await AnswersAsync(http, url, ct)) return new(true, null);
            var exited = _started.FindIndex(p => p.HasExited);
            if (exited >= 0) return new(false, $"the app exited (code {_started[exited].ExitCode}) before it answered at {baseUrl} (see {LogPath(exited)})");
            if (DateTime.UtcNow >= deadline) return new(false, notUp);
            await Task.Delay(250, ct);
        }
    }

    private string LogPath(int i) => Path.Combine(logDir, $"app-{i + 1}.log");

    private static async Task<bool> AnswersAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        try { using var _ = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); return true; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    /// Kills every started process tree and waits for each to exit; safe to call again.
    public async Task StopAsync()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token); } catch (OperationCanceledException) { }
            p.Dispose();
        }
        // SIGKILL reaches the children a moment after Kill returns: wait for the port to be released, so "stopped" means "free".
        if (_started.Count > 0)
            for (var i = 0; i < 40 && IsServing(new Uri(baseUrl)); i++) await Task.Delay(250);
        _started.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
