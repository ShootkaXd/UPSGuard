using System.Net;

namespace UPSGuard.Service;

public sealed class HealthIpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HealthIpAllowlistMiddleware> _logger;
    private readonly HealthIpAllowlist _allowlist;

    public HealthIpAllowlistMiddleware(
        RequestDelegate next,
        ILogger<HealthIpAllowlistMiddleware> logger,
        HealthIpAllowlist allowlist)
    {
        _next = next;
        _logger = logger;
        _allowlist = allowlist;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (!path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress;

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            await context.Response.WriteAsync("Method Not Allowed");
            return;
        }

        if (!_allowlist.IsAllowed(remoteIp))
        {
            _logger.LogWarning("Forbidden IP: {IP}", FormatIp(remoteIp));

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden");
            return;
        }

        if (!ApiKeyAuth.IsValid(context))
        {
            _logger.LogWarning("Invalid API key from IP: {IP}", FormatIp(remoteIp));

            if (!context.Request.Headers.ContainsKey(ApiKeyAuth.GetHeaderName()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Missing API Key");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Invalid API Key");
            return;
        }

        _logger.LogInformation("Health request allowed from IP: {IP}", FormatIp(remoteIp));

        await _next(context);
    }

    private static string FormatIp(IPAddress? ip)
    {
        return ip?.ToString() ?? "<null>";
    }
}