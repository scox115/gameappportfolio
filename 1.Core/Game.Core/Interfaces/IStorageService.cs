namespace Game.Core.Interfaces;

public interface IStorageService
{
    /// <summary>
    /// Uploads a file stream to cloud object storage under a unique name ending in
    /// <paramref name="fileName"/> and returns the public URI.
    /// </summary>
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string containerName, string contentType);

    /// <summary>
    /// Deletes a file this service uploaded to <paramref name="containerName"/>. URLs that point
    /// anywhere else are ignored, so a stale or external address can't delete someone else's file.
    /// </summary>
    Task DeleteFileAsync(string fileUrl, string containerName);
}
