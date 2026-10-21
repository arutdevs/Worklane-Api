using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Services;

internal static class WorkspaceSlug
{
    public const string Fallback = "workspace";
    public const int MaxLength = 100;

    public static string ForDisplayName(string? displayName)
    {
        var name = Normalize(displayName);
        return name is null ? Fallback : $"{name}-workspace";
    }

    public static string ForWorkspaceName(string? name) => Normalize(name) ?? Fallback;

    public static async Task<string> GenerateUniqueAsync(
        IQueryable<Workspace> workspaces,
        string baseSlug,
        CancellationToken cancellationToken)
    {
        var slug = Fit(baseSlug, 0);
        var candidate = slug;
        var suffix = 0;
        while (await workspaces.AnyAsync(w => w.Slug == candidate, cancellationToken))
        {
            suffix++;
            candidate = Fit(slug, suffix);
        }

        return candidate;
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var lowered = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);
        var pendingDash = false;
        foreach (var c in lowered)
        {
            if (IsSlugChar(c))
            {
                if (pendingDash && builder.Length > 0)
                    builder.Append('-');
                pendingDash = false;
                builder.Append(c);
                continue;
            }

            pendingDash = true;
        }

        var text = builder.ToString();
        if (text.Length == 0 || !ContainsLetterOrDigit(text))
            return null;

        return text;
    }

    private static string Fit(string slug, int suffix)
    {
        var tail = suffix == 0 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture);
        var room = MaxLength - tail.Length;
        var head = slug;
        if (head.Length > room)
            head = head[..room].TrimEnd('-');
        while (head.Length > 0 && char.IsLowSurrogate(head[^1]))
            head = head[..^1];

        if (head.Length == 0 || !ContainsLetterOrDigit(head))
            head = Fallback.Length <= room ? Fallback : Fallback[..room];

        return head + tail;
    }

    private static bool IsSlugChar(char c)
    {
        if (char.IsLetterOrDigit(c))
            return true;

        // Thai vowels and tone marks are combining marks. IsLetterOrDigit drops them,
        // so "พี่" used to collapse to "พ".
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    private static bool ContainsLetterOrDigit(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                return true;
        }

        return false;
    }
}
