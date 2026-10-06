using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game.Api.Health;

/// <summary>Only avatar uploads need blob storage, so an outage is Degraded rather than Unhealthy.</summary>
public class BlobStorageHealthCheck(BlobServiceClient blobService, ILogger<BlobStorageHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // The storage SDK retries for several seconds by default; a probe should answer quickly.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await blobService.GetPropertiesAsync(timeout.Token);
            return HealthCheckResult.Healthy("Blob storage reachable.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Blob storage health check failed.");
            return HealthCheckResult.Degraded("Blob storage unreachable; avatar uploads will fail.");
        }
    }
}
