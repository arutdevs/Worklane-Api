using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;

namespace Worklane.Infrastructure.Services;

public static class FileStorageRegistration
{
    public static IServiceCollection AddWorklaneFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FileStorageProviderSettings>()
            .Bind(configuration.GetSection(FileStorageProviderSettings.SectionName))
            .Validate(
                static settings => FileStorageProviders.IsKnown(settings.Provider),
                "FileStorage:Provider must be Local or S3.")
            .ValidateOnStart();

        var s3 = services.AddOptions<S3StorageOptions>()
            .Bind(configuration.GetSection(S3StorageOptions.SectionName));

        var provider = configuration.GetSection(FileStorageSettings.SectionName)[nameof(FileStorageProviderSettings.Provider)];
        if (FileStorageProviders.IsS3(provider))
        {
            services.AddSingleton<IValidateOptions<S3StorageOptions>, S3StorageOptionsValidator>();
            s3.ValidateOnStart();
            services.AddSingleton<IAmazonS3>(sp =>
                S3ClientFactory.Create(sp.GetRequiredService<IOptions<S3StorageOptions>>().Value));
            services.AddScoped<IFileStorage, S3FileStorage>();
        }
        else
        {
            services.AddScoped<IFileStorage, LocalFileStorage>();
        }

        return services;
    }
}
