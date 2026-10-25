using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Worklane.Infrastructure.Services;

/// <summary>
/// Signup locale for the personal workspace name and, later, the columns of projects
/// that user creates. English is the default. Only <c>th</c> selects Thai.
/// </summary>
public static class WorkspaceLocale
{
    public const string English = "en";
    public const string Thai = "th";

    public static readonly IReadOnlyList<ColumnSeed> EnglishColumns =
    [
        new("To Do", 1000, false, false),
        new("In Progress", 2000, false, true),
        new("Done", 3000, true, false)
    ];

    public static readonly IReadOnlyList<ColumnSeed> ThaiColumns =
    [
        new("ต้องทำ", 1000, false, false),
        new("กำลังทำ", 2000, false, true),
        new("เสร็จแล้ว", 3000, true, false)
    ];

    public readonly record struct ColumnSeed(string Name, int Order, bool IsDone, bool IsInProgress);

    public static bool IsThai(string? language) =>
        string.Equals(language, Thai, StringComparison.Ordinal);

    public static IReadOnlyList<ColumnSeed> ColumnsFor(string? language) =>
        IsThai(language) ? ThaiColumns : EnglishColumns;

    public static string WorkspaceName(string displayName, string language) =>
        IsThai(language) ? $"เวิร์กสเปซของ {displayName}" : $"{displayName}'s Workspace";

    /// <summary>
    /// A non-blank <paramref name="preferredLanguage"/> is authoritative: a primary tag of
    /// <c>th</c> (including <c>th-TH</c>) selects Thai, and every other explicit value selects
    /// English. When the field is omitted, the highest-quality <paramref name="acceptLanguage"/>
    /// range whose primary tag is <c>th</c> or <c>en</c> wins. Unsupported ranges are skipped.
    /// Anything else, including a missing header, is English.
    /// </summary>
    public static string Resolve(string? preferredLanguage, string? acceptLanguage)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
            return IsThaiTag(preferredLanguage) ? Thai : English;

        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return English;

        var parts = acceptLanguage.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !StringWithQualityHeaderValue.TryParseList(parts, out IList<StringWithQualityHeaderValue>? ranges)
            || ranges is null)
        {
            return English;
        }

        foreach (var range in ranges
            .Select(range => (Tag: range.Value.Value, Quality: range.Quality.GetValueOrDefault(1)))
            .Where(range => range.Quality > 0 && !string.IsNullOrEmpty(range.Tag))
            .OrderByDescending(range => range.Quality))
        {
            if (IsThaiTag(range.Tag))
                return Thai;
            if (IsEnglishTag(range.Tag))
                return English;
        }

        return English;
    }

    private static bool IsThaiTag(string? tag) =>
        string.Equals(Primary(tag), Thai, StringComparison.Ordinal);

    private static bool IsEnglishTag(string? tag) =>
        string.Equals(Primary(tag), English, StringComparison.Ordinal);

    private static string Primary(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "";

        var value = tag.Trim();
        if (value.Length > 35)
            value = value[..35];

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var primary = dash > 0 ? value[..dash] : value;
        return primary.ToLower(CultureInfo.InvariantCulture);
    }
}
