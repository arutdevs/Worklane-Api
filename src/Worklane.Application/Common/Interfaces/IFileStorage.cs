namespace Worklane.Application.Common.Interfaces;

/// <summary>
/// Blob storage for task attachments. The local-disk implementation is the default;
/// an S3 or Azure Blob implementation can replace it without touching callers.
/// Keys are produced by the application, never by the client.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Writes the stream. Throws <see cref="Exceptions.PayloadTooLargeException"/>
    /// and leaves nothing behind when the stream exceeds <paramref name="maxBytes"/>.
    /// </summary>
    Task<long> SaveAsync(string storageKey, Stream content, long maxBytes, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
