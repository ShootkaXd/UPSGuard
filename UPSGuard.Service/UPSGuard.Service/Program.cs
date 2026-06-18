using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace UPSGuard.Service;

internal static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseWindowsService(options =>
        {
            options.ServiceName = "UPSGuardService";
        });

        builder.WebHost.UseUrls("http://+:18080");

        builder.Services.AddSingleton<UpsGuardState>();
        builder.Services.AddSingleton<HealthIpAllowlist>();
        builder.Services.AddSingleton<ServiceLogger>();
        builder.Services.AddSingleton<UserSessionLauncher>();

        builder.Services.AddHostedService<Worker>();
        builder.Services.AddHostedService<NotifyHostWatchdog>();

        builder.Services
            .AddHealthChecks()
            .AddCheck<UpsGuardHealthCheck>("upsguard");

        var app = builder.Build();

        app.UseMiddleware<HealthIpAllowlistMiddleware>();

        app.MapGet("/", () => Results.Text("UPSGuard Service is running", "text/plain"));

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                var text = report.Status == HealthStatus.Healthy ? "OK" : "UNHEALTHY";
                await context.Response.WriteAsync(text);
            }
        });

        app.Run();
    }
}