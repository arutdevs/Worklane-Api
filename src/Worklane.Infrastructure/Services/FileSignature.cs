using System.Text;

namespace Worklane.Infrastructure.Services;

/// <summary>
/// Decides whether a file's bytes match the type implied by its extension.
/// The client Content-Type is never consulted.
/// </summary>
internal static class FileSignature
{
    /// <summary>How many leading bytes decide the binary signature. Text types are scanned for the whole stream.</summary>
    public const int HeaderLength = 8192;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool Matches(string extension, ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty) return false;

        return extension switch
        {
            ".png" => StartsWith(content, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ".jpg" or ".jpeg" => StartsWith(content, [0xFF, 0xD8, 0xFF]),
            ".gif" => StartsWith(content, "GIF87a"u8) || StartsWith(content, "GIF89a"u8),
            ".webp" => StartsWith(content, "RIFF"u8) && content.Length >= 12 && content.Slice(8, 4).SequenceEqual("WEBP"u8),
            ".pdf" => StartsWith(content, "%PDF-"u8),
            ".zip" or ".docx" or ".xlsx" or ".pptx" => IsZip(content),
            ".doc" or ".xls" or ".ppt" => StartsWith(content, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]),
            ".txt" or ".csv" => IsText(content),
            ".json" => IsJson(content),
            _ => false,
        };
    }

    public static string? ContentTypeForExtension(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".xls" => "application/vnd.ms-excel",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => null,
    };

    private static bool IsZip(ReadOnlySpan<byte> content) =>
        StartsWith(content, [0x50, 0x4B, 0x03, 0x04]) ||
        StartsWith(content, [0x50, 0x4B, 0x05, 0x06]) ||
        StartsWith(content, [0x50, 0x4B, 0x07, 0x08]);

    private static bool IsText(ReadOnlySpan<byte> content)
    {
        if (HasUtf16Bom(content))
            return IsPlainText(DecodeUtf16(content));

        if (TryDecodeUtf8(content, out var utf8))
            return IsPlainText(utf8);

        return IsThaiSingleByte(content);
    }

    private static bool IsJson(ReadOnlySpan<byte> content)
    {
        if (!IsText(content)) return false;

        string text;
        if (HasUtf16Bom(content))
            text = DecodeUtf16(content);
        else if (!TryDecodeUtf8(content, out text!))
            return false;

        text = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text.Length > 0 && text[0] is '{' or '[';
    }

    private static bool HasUtf16Bom(ReadOnlySpan<byte> content) =>
        content.Length >= 2 &&
        ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF));

    private static string DecodeUtf16(ReadOnlySpan<byte> content)
    {
        var encoding = content[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
        return encoding.GetString(content);
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> content, out string text)
    {
        try
        {
            text = Utf8.GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// TIS-620 and windows-874 (Excel Thai) are single-byte. Printable ASCII, tab, CR, LF,
    /// and bytes 0x80-0xFF are accepted. Markup is still rejected from the ASCII bytes.
    /// </summary>
    private static bool IsThaiSingleByte(ReadOnlySpan<byte> content)
    {
        Span<char> ascii = content.Length <= HeaderLength
            ? stackalloc char[content.Length]
            : new char[content.Length];

        for (var i = 0; i < content.Length; i++)
        {
            var b = content[i];
            if (b is 0x09 or 0x0A or 0x0D || b is >= 0x20 and <= 0x7E)
            {
                ascii[i] = (char)b;
                continue;
            }

            if (b >= 0x80)
            {
                ascii[i] = ' ';
                continue;
            }

            return false;
        }

        return !ContainsActiveContent(new string(ascii));
    }

    private static bool IsPlainText(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not '\t' and not '\n' and not '\r')
                return false;
        }

        return !ContainsActiveContent(text);
    }

    internal static bool ContainsActiveContent(string text) =>
        text.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("</script", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<iframe", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<svg", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<object", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<embed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("onerror=", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("onload=", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> magic) =>
        content.Length >= magic.Length && content[..magic.Length].SequenceEqual(magic);
}
