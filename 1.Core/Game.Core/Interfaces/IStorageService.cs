namespace Game.Core.Interfaces;

public interface IStorageService
{
    /// <summary>
    /// Uploads a file stream to cloud object storage and returns the public URI.
    /// </summary>
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string containerName);
}
