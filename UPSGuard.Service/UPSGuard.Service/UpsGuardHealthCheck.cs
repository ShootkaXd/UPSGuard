using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace UPSGuard.Service;

public sealed class UpsGuardHealthCheck : IHealthCheck
{
    private readonly UpsGuardState _state;

    public UpsGuardHealthCheck(UpsGuardState state)
    {
        _state = state;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_state.IsStarted)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                description: "Service has not finished startup yet."));
        }

        if (!_state.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                description: _state.LastError ?? "Service is unhealthy."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            description: "UPSGuard service is running normally."));
    }
}