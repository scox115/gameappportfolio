using Game.Api.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game.Api.Health;

/// <summary>
/// Reports the telemetry sender's connection instead of opening a new one on every probe. Match
/// events wait in memory while RabbitMQ is down, so this is Degraded, never Unhealthy.
/// </summary>
public class MessageBrokerHealthCheck(TelemetryBrokerStatus status, MatchTelemetryPublisher publisher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var pending = (publisher.Pending.CanCount ? publisher.Pending.Count : 0) + status.HeldEvents;
        var data = new Dictionary<string, object> { ["pendingEvents"] = pending };

        return Task.FromResult(status.IsConnected
            ? HealthCheckResult.Healthy("Connected to RabbitMQ.", data)
            : HealthCheckResult.Degraded($"Not connected to RabbitMQ; {pending} match event(s) waiting.", data: data));
    }
}
