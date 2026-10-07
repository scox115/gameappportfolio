using Azure;
using Azure.Storage.Blobs;
using Game.Api.Endpoints;
using Game.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Api.Tests;

public class BlobStorageHealthCheckTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)] // the first upload creates the container
    public async Task Healthy_WhenTheAvatarContainerCanBeRead(bool exists)
    {
        var blobs = new FakeBlobService((_, _) => Task.FromResult(exists));

        var result = await CheckAsync(blobs);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal([PlayerEndpoints.AvatarContainer], blobs.ContainersRead);
    }

    [Fact]
    public async Task Degraded_WhenStorageRefusesTheApiIdentity()
    {
        // What Azure answers when the managed identity's role doesn't cover the request.
        var blobs = new FakeBlobService((_, _) => throw new RequestFailedException(403,
            "This request is not authorized to perform this operation using this permission.", "AuthorizationPermissionMismatch", null));

        var result = await CheckAsync(blobs);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Blob storage refused the API's identity; avatar uploads will fail.", result.Description);
    }

    [Fact]
    public async Task Degraded_WhenStorageIsUnreachable()
    {
        var blobs = new FakeBlobService((_, _) => throw new RequestFailedException("No such host is known."));

        var result = await CheckAsync(blobs);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Blob storage unreachable; avatar uploads will fail.", result.Description);
    }

    [Fact]
    public async Task Degraded_WhenStorageIsTooSlowToAnswer()
    {
        var blobs = new FakeBlobService(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        });

        var result = await CheckAsync(blobs);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    private static Task<HealthCheckResult> CheckAsync(BlobServiceClient blobs) =>
        new BlobStorageHealthCheck(blobs, NullLogger<BlobStorageHealthCheck>.Instance).CheckHealthAsync(new HealthCheckContext());

    private class FakeBlobService(Func<string, CancellationToken, Task<bool>> exists) : BlobServiceClient
    {
        public List<string> ContainersRead { get; } = [];

        private Task<bool> ExistsAsync(string name, CancellationToken cancellationToken)
        {
            ContainersRead.Add(name);
            return exists(name, cancellationToken);
        }

        public override BlobContainerClient GetBlobContainerClient(string blobContainerName) => new FakeContainer(this, blobContainerName);

        private class FakeContainer(FakeBlobService service, string name) : BlobContainerClient
        {
            public override async Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default)
            {
                return Response.FromValue(await service.ExistsAsync(name, cancellationToken), null!);
            }
        }
    }
}
