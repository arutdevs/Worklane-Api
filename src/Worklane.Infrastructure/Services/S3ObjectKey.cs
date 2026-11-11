namespace Worklane.Infrastructure.Services;

/// <summary>
/// Turns an application storage key into an object key. Rejects traversal and rooted paths.
/// </summary>
internal static class S3ObjectKey
{
    public const int MaxLength = 1024;

    public static string Normalize(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || !string.Equals(storageKey, storageKey.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid storage key.");

        if (storageKey.Length > MaxLength
            || storageKey.Contains('\\', StringComparison.Ordinal)
            || storageKey.Contains(':', StringComparison.Ordinal)
            || storageKey.Contains("..", StringComparison.Ordinal)
            || storageKey.Contains("//", StringComparison.Ordinal)
            || Path.IsPathRooted(storageKey))
        {
            throw new InvalidOperationException("Invalid storage key.");
        }

        var segments = storageKey.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                throw new InvalidOperationException("Invalid storage key.");

            foreach (var ch in segment)
            {
                if (char.IsControl(ch))
                    throw new InvalidOperationException("Invalid storage key.");
            }
        }

        return storageKey;
    }
}
