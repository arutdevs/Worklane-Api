using System.Text;

namespace Worklane.Infrastructure.Services;

/// <summary>
/// Decides whether a file's bytes match the type implied by its extension.
/// The client Content-Type is never consulted.
/// </summary>
internal static class FileSignature
{
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
        try
        {
            var text = Utf8.GetString(content);
            foreach (var ch in text)
            {
                if (char.IsControl(ch) && ch is not '\t' and not '\n' and not '\r')
                    return false;
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsJson(ReadOnlySpan<byte> content)
    {
        if (!IsText(content)) return false;
        var text = Utf8.GetString(content).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text.Length > 0 && text[0] is '{' or '[';
    }

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> magic) =>
        content.Length >= magic.Length && content[..magic.Length].SequenceEqual(magic);
}
