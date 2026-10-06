using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Game.Core.Interfaces;
using System.Text.RegularExpressions;

namespace Game.Infrastructure.Storage;

public class AzureBlobStorageService : IStorageService
{
    private readonly BlobServiceClient _blobServiceClient;

    public AzureBlobStorageService(BlobServiceClient blobServiceClient)
    {
        _blobServiceClient = blobServiceClient;
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string containerName, string contentType)
    {
        var containerClient = ContainerFor(containerName);

        // Ensure the container exists locally or in the cloud
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

        // A unique name per upload, so a new file never overwrites an old one and can be cached forever
        var uniqueFileName = $"{Guid.NewGuid()}_{Path.GetFileName(fileName)}";
        var blobClient = containerClient.GetBlobClient(uniqueFileName);

        await blobClient.UploadAsync(fileStream, new BlobHttpHeaders
        {
            ContentType = contentType,
            CacheControl = "public, max-age=31536000, immutable"
        });

        return blobClient.Uri.ToString();
    }

    public async Task DeleteFileAsync(string fileUrl, string containerName)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var fileUri)) return;

        var containerClient = ContainerFor(containerName);
        var containerPath = containerClient.Uri.AbsolutePath.TrimEnd('/') + "/";
        if (!string.Equals(fileUri.GetLeftPart(UriPartial.Authority), containerClient.Uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || !fileUri.AbsolutePath.StartsWith(containerPath, StringComparison.Ordinal))
        {
            return;
        }

        var blobName = Uri.UnescapeDataString(fileUri.AbsolutePath[containerPath.Length..]);
        if (blobName.Length == 0 || blobName.Contains('/')) return;

        await containerClient.GetBlobClient(blobName).DeleteIfExistsAsync();
    }

    // Strict Azure rule: lowercase, alphanumeric characters and hyphens only
    private BlobContainerClient ContainerFor(string containerName) =>
        _blobServiceClient.GetBlobContainerClient(Regex.Replace(containerName.ToLower(), @"[^a-z0-9\-]", ""));
}
