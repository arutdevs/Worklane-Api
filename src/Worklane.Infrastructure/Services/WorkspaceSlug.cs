using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Worklane.Application.Common.Exceptions;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

internal static class WorkspaceSlug
{
    public const string Fallback = "workspace";
    public const int MaxLength = 100;
    public const int MaxInsertAttempts = 8;
    public const string UniqueIndexName = "IX_Workspaces_Slug";

    public static string ForDisplayName(string? displayName)
    {
        var name = Normalize(displayName);
        return name is null ? Fallback : $"{name}-workspace";
    }

    public static string ForWorkspaceName(string? name) => Normalize(name) ?? Fallback;

    public static async Task<UniqueSlug> GenerateUniqueAsync(
        IQueryable<Workspace> workspaces,
        string baseSlug,
        CancellationToken cancellationToken,
        int minimumSuffix = 0)
    {
        var slug = Fit(baseSlug, 0);
        var suffix = minimumSuffix < 0 ? 0 : minimumSuffix;
        var candidate = suffix == 0 ? slug : Fit(slug, suffix);
        while (await workspaces.AnyAsync(w => w.Slug == candidate, cancellationToken))
        {
            suffix++;
            candidate = Fit(slug, suffix);
        }

        return new UniqueSlug(candidate, suffix);
    }

    /// <summary>
    /// Inserts inside a transaction and retries when <see cref="UniqueIndexName"/> loses a race.
    /// The callback must add the workspace (and anything that has to commit with it) and save.
    /// The caller wraps this in <see cref="DbRetry"/> when a retrying provider is enabled.
    /// </summary>
    internal static async Task<T> InsertAsync<T>(
        WorklaneDbContext db,
        string baseSlug,
        Func<UniqueSlug, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken)
    {
        var minimumSuffix = 0;
        for (var attempt = 0; attempt < MaxInsertAttempts; attempt++)
        {
            db.ChangeTracker.Clear();
            var attemptedSuffix = minimumSuffix;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var slug = await GenerateUniqueAsync(db.Workspaces, baseSlug, cancellationToken, minimumSuffix);
                attemptedSuffix = slug.Suffix;
                var result = await write(slug, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception ex) when (UniqueConstraintConflicts.Is(ex, UniqueIndexName))
            {
                minimumSuffix = attemptedSuffix + 1;
            }
        }

        throw new ConflictException(
            UniqueConstraintConflicts.WorkspaceSlugUnavailableCode,
            UniqueConstraintConflicts.WorkspaceSlugUnavailableDetail);
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var lowered = CompatibilityFold(value.Trim()).ToLowerInvariant();
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

    /// <summary>
    /// NFKC folds fullwidth ASCII, but it also splits Thai SARA AM (U+0E33) into
    /// NIKHAHIT + SARA AA because that character is composition-excluded. Thai
    /// (U+0E00–U+0E7F) is copied unchanged. Everything else is still compatibility-folded.
    /// </summary>
    private static string CompatibilityFold(string value)
    {
        var folded = new StringBuilder(value.Length);
        var segment = new StringBuilder();
        foreach (var c in value)
        {
            if (IsThai(c))
            {
                AppendFolded(folded, segment);
                folded.Append(c);
                continue;
            }

            segment.Append(c);
        }

        AppendFolded(folded, segment);
        return folded.ToString();
    }

    private static void AppendFolded(StringBuilder folded, StringBuilder segment)
    {
        if (segment.Length == 0)
            return;

        folded.Append(segment.ToString().Normalize(NormalizationForm.FormKC));
        segment.Clear();
    }

    private static bool IsThai(char c) => c is >= '\u0E00' and <= '\u0E7F';

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

    internal readonly record struct UniqueSlug(string Value, int Suffix);
}
