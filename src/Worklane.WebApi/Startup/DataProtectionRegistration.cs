using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Worklane.WebApi.Startup;

public static class DataProtectionRegistration
{
    public const string ApplicationName = "Worklane";
    public const string KeysPathKey = "DataProtection:KeysPath";

    public static IServiceCollection AddWorklaneDataProtection(
        this IServiceCollection services,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        var path = configuration[KeysPathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            {
                if (environment.IsEnvironment("Testing"))
                    builder.UseEphemeralDataProtectionProvider();

                return services;
            }

            throw new InvalidOperationException(
                "DataProtection:KeysPath is not set. Outside Development and Testing the API will not start, " +
                "because Data Protection keys must be stored on a writable persistent volume. " +
                "Set DataProtection__KeysPath (production uses /var/lib/worklane/dataprotection-keys).");
        }

        EnsureWritable(path);
        builder.PersistKeysToFileSystem(new DirectoryInfo(path));
        return services;
    }

    private static void EnsureWritable(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"DataProtection key directory '{path}' does not exist. Create it on the persistent volume before start. " +
                "The process runs as uid 1654 and the root filesystem is read-only, so this path is not created at startup.");
        }

        var probe = Path.Combine(path, ".worklane-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"DataProtection key directory '{path}' is not writable by the current process. " +
                "The container runs as uid 1654 and the root filesystem is read-only. " +
                "Mount a writable volume at DataProtection:KeysPath.",
                ex);
        }
    }
}
