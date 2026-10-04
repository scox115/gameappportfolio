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

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string containerName)
    {
        // Strict Azure rule: Lowercase, alphanumeric characters and hyphens only
        var cleanContainerName = Regex.Replace(containerName.ToLower(), @"[^a-z0-9\-]", "");

        // 1. Get a reference to the target asset container
        var containerClient = _blobServiceClient.GetBlobContainerClient(cleanContainerName);
        
        // 2. Ensure the container exists locally or in the cloud
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

        // 3. Define a unique file name path using a Guid to avoid overwrites
        var uniqueFileName = $"{Guid.NewGuid()}_{Path.GetFileName(fileName)}";
        var blobClient = containerClient.GetBlobClient(uniqueFileName);

        // 4. Stream the data up to the Azurite container engine
        await blobClient.UploadAsync(fileStream, new BlobHttpHeaders { ContentType = "image/png" });

        // 5. Return the accessible endpoint address
        return blobClient.Uri.ToString();
    }
}
