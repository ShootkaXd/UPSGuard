using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UPSGuard.Service;

public sealed class HealthServer : IDisposable
{
    private readonly HttpListener _listener = new HttpListener();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly Func<bool> _isHealthy;

    public int Port { get; }

    public HealthServer(int port = 18080, Func<bool>? isHealthy = null)
    {
        Port = port;
        _isHealthy = isHealthy ?? (() => true);

        _listener.Prefixes.Add($"http://+:{Port}/health/");
    }

    public void Start()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        _listener.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_cts == null) return;

        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }

        try { _loop?.Wait(2000); } catch { }
        try { _cts.Dispose(); } catch { }

        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;

            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (!path.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/health/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            bool healthy = _isHealthy();
            ctx.Response.StatusCode = healthy ? 200 : 503;

            var body = healthy ? "OK" : "UNHEALTHY";
            byte[] bytes = Encoding.UTF8.GetBytes(body);

            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;

            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
