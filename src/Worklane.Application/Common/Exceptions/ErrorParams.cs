namespace Worklane.Application.Common.Exceptions;

/// <summary>Builds the parameter dictionary copied onto a problem response.</summary>
public static class ErrorParams
{
    public static IReadOnlyDictionary<string, object?> Of(params (string Key, object? Value)[] pairs)
    {
        var result = new Dictionary<string, object?>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            result[key] = value;
        return result;
    }
}
