using Game.Api.Messaging;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game.Api.Health;

/// <summary>
/// Reports the outbox relay's connection instead of opening a new one on every probe, plus how many
/// match events are waiting in the outbox. Events wait safely in the database while RabbitMQ is
/// down, so this is Degraded, never Unhealthy.
/// </summary>
public class MessageBrokerHealthCheck(TelemetryBrokerStatus status, AppDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var pending = "an unknown number of";
        try
        {
            var count = await dbContext.OutboxMessages.CountAsync(cancellationToken);
            data["pendingEvents"] = count;
            pending = count.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database check reports this; the broker's own state is still worth showing.
        }

        return status.IsConnected
            ? HealthCheckResult.Healthy("Connected to RabbitMQ.", data)
            : HealthCheckResult.Degraded($"Not connected to RabbitMQ; {pending} match event(s) waiting in the outbox.", data: data);
    }
}
