using Amazon.Runtime;
using Amazon.S3;
using Worklane.Application.Common.Options;

namespace Worklane.Infrastructure.Services;

internal static class S3ClientFactory
{
    public static AmazonS3Client Create(S3StorageOptions options)
    {
        var serviceUrl = options.ServiceUrl.Trim();
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region.Trim(),
            // R2 and RustFS reject the SDK's default CRC32 request and response checksums.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey.Trim(), options.SecretKey.Trim()),
            config);
    }
}
