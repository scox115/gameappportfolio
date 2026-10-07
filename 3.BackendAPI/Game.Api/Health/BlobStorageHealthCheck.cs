using Azure;
using Azure.Storage.Blobs;
using Game.Api.Endpoints;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game.Api.Health;

/// <summary>Only avatar uploads need blob storage, so an outage is Degraded rather than Unhealthy.</summary>
/// <remarks>
/// The check reads the avatar container, the same permission an upload needs. It used to read the
/// account's service properties, which the API's role in Azure (Storage Blob Data Contributor) isn't
/// allowed to do, so every Azure deploy reported storage as down even though uploads didn't need that.
/// </remarks>
public class BlobStorageHealthCheck(BlobServiceClient blobService, ILogger<BlobStorageHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // The storage SDK retries for several seconds by default; a probe should answer quickly.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            // The container is created by the first upload, so "not there yet" is still healthy.
            await blobService.GetBlobContainerClient(PlayerEndpoints.AvatarContainer).ExistsAsync(timeout.Token);
            return HealthCheckResult.Healthy("Blob storage reachable.");
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            logger.LogWarning(ex, "Blob storage refused the API's identity ({ErrorCode}).", ex.ErrorCode);
            return HealthCheckResult.Degraded("Blob storage refused the API's identity; avatar uploads will fail.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Blob storage health check failed.");
            return HealthCheckResult.Degraded("Blob storage unreachable; avatar uploads will fail.");
        }
    }
}
