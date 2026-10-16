using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Factory.Integration.Tests.Support;

/// A local fake of a model API: answers every POST with `reply` and keeps each request body.
public sealed class FakeModelServer : IDisposable
{
    private readonly HttpListener _listener;
    public List<string> Bodies { get; } = [];
    public Uri Url { get; }

    public FakeModelServer(string reply)
    {
        // A free port can be taken by a parallel test between the probe and the bind, so a failed bind tries another.
        for (var attempt = 1; ; attempt++)
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException) when (attempt < 5) { listener.Close(); continue; }
            (_listener, Url) = (listener, new Uri($"http://127.0.0.1:{port}/"));
            break;
        }
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                using (var r = new StreamReader(ctx.Request.InputStream)) Bodies.Add(await r.ReadToEndAsync());
                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _listener.Close();
}
