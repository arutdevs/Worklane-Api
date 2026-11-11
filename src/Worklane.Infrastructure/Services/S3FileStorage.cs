using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;

namespace Worklane.Infrastructure.Services;

/// <summary>
/// S3-compatible storage for RustFS and Cloudflare R2. The caller stream is not seekable
/// and has no length, so bytes are copied to a temp file in chunks and then uploaded with
/// a known content length. The object is private: no ACL, content type octet-stream.
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    public const string StoredContentType = "application/octet-stream";
    public const string StoredContentDisposition = "attachment";

    private readonly IAmazonS3 _client;
    private readonly S3StorageOptions _options;
    private readonly ILogger<S3FileStorage> _logger;
    private readonly string _tempDirectory;

    public S3FileStorage(IAmazonS3 client, IOptions<S3StorageOptions> options, ILogger<S3FileStorage> logger)
        : this(client, options, logger, Path.Combine(Path.GetTempPath(), "worklane-s3"))
    {
    }

    internal S3FileStorage(
        IAmazonS3 client,
        IOptions<S3StorageOptions> options,
        ILogger<S3FileStorage> logger,
        string tempDirectory)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
        _tempDirectory = tempDirectory;
    }

    public async Task<long> SaveAsync(string storageKey, Stream content, long maxBytes, CancellationToken cancellationToken = default)
    {
        var key = S3ObjectKey.Normalize(storageKey);
        ArgumentNullException.ThrowIfNull(content);

        Directory.CreateDirectory(_tempDirectory);
        var tempPath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            long total = 0;
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        throw new PayloadTooLargeException(maxBytes);

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            await using var input = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var request = CreatePutRequest(_options, key, input, total);
            try
            {
                await _client.PutObjectAsync(request, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (S3StorageErrors.IsPreconditionFailed(ex))
            {
                throw new InvalidOperationException("Storage key already exists.", ex);
            }
            catch (OperationCanceledException)
            {
                await DeleteQuietlyAsync(key);
                throw;
            }

            return total;
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var key = S3ObjectKey.Normalize(storageKey);
        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Bucket,
                Key = key,
            }, cancellationToken);
            return new S3ObjectReadStream(response);
        }
        catch (AmazonS3Exception ex)
        {
            throw S3StorageErrors.MapRead(ex);
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var key = S3ObjectKey.Normalize(storageKey);
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Bucket,
                Key = key,
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (S3StorageErrors.IsMissingObject(ex))
        {
        }
    }

    internal static PutObjectRequest CreatePutRequest(S3StorageOptions options, string key, Stream body, long length)
    {
        var request = new PutObjectRequest
        {
            BucketName = options.Bucket.Trim(),
            Key = key,
            InputStream = body,
            AutoCloseStream = false,
            ContentType = StoredContentType,
            UseChunkEncoding = false,
            DisableDefaultChecksumValidation = true,
            // Unsigned payloads are HTTPS-only in this SDK, and RustFS dev is http.
            // Checksums stay off via the client config and UseChunkEncoding=false.
            DisablePayloadSigning = false,
            IfNoneMatch = "*",
        };
        request.Headers.ContentDisposition = StoredContentDisposition;
        request.Headers.ContentLength = length;
        return request;
    }

    private string Bucket => _options.Bucket.Trim();

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Bucket,
                Key = key,
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not remove object {Key} after a cancelled upload", key);
        }
    }

    private void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete storage spool {Path}", path);
        }
    }
}
