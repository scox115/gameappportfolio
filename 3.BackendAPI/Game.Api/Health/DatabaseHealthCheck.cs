using Game.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game.Api.Health;

/// <summary>The API can't sign anyone in or save a battle without the database, so it's critical.</summary>
public class DatabaseHealthCheck(AppDbContext dbContext, ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : new HealthCheckResult(context.Registration.FailureStatus, "Database unreachable.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database health check failed.");
            return new HealthCheckResult(context.Registration.FailureStatus, "Database unreachable.");
        }
    }
}
