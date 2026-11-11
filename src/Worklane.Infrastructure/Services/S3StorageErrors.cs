using System.Net;
using Amazon.S3;
using Worklane.Application.Common.Exceptions;

namespace Worklane.Infrastructure.Services;

internal static class S3StorageErrors
{
    public static bool IsMissingObject(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound
        || exception.ErrorCode is "NoSuchKey" or "NotFound";

    public static bool IsPreconditionFailed(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.PreconditionFailed
        || string.Equals(exception.ErrorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase);

    public static Exception MapRead(AmazonS3Exception exception) =>
        IsMissingObject(exception)
            ? new NotFoundException("The stored file is missing.")
            : exception;
}
