using System.Text;
using Worklane.Application.Common.Exceptions;

namespace Worklane.Infrastructure.Services;

/// <summary>
/// Scans a text upload one chunk at a time. A short tail is kept so a marker
/// split across two reads is still visible. The file itself is not buffered again.
/// </summary>
internal sealed class StreamingTextChecker
{
    private const int TailChars = 10;

    private readonly string _extension;
    private readonly Decoder? _decoder;
    private readonly bool _thai;
    private readonly char[] _tail = new char[TailChars];
    private int _tailLength;

    private StreamingTextChecker(string extension, Decoder? decoder, bool thai)
    {
        _extension = extension;
        _decoder = decoder;
        _thai = thai;
    }

    public static bool RequiresFullScan(string extension) =>
        extension is ".txt" or ".csv" or ".json";

    public static StreamingTextChecker Create(string extension, ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0xFE)
            return new StreamingTextChecker(extension, Encoding.Unicode.GetDecoder(), thai: false);
        if (header.Length >= 2 && header[0] == 0xFE && header[1] == 0xFF)
            return new StreamingTextChecker(extension, Encoding.BigEndianUnicode.GetDecoder(), thai: false);

        if (IsStrictUtf8(header))
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return new StreamingTextChecker(extension, utf8.GetDecoder(), thai: false);
        }

        return new StreamingTextChecker(extension, decoder: null, thai: true);
    }

    public void Accept(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
            return;

        ReadOnlySpan<char> text = _thai ? DecodeThai(chunk) : Decode(chunk);
        if (HasDisallowedControl(text) || ContainsMarker(_tail.AsSpan(0, _tailLength), text))
            throw new UnsupportedMediaTypeException(_extension);

        Remember(text);
    }

    private char[] DecodeThai(ReadOnlySpan<byte> chunk)
    {
        var chars = new char[chunk.Length];
        for (var i = 0; i < chunk.Length; i++)
        {
            var b = chunk[i];
            if (b is 0x09 or 0x0A or 0x0D || b is >= 0x20 and <= 0x7E)
            {
                chars[i] = (char)b;
                continue;
            }

            if (b >= 0x80)
            {
                chars[i] = ' ';
                continue;
            }

            throw new UnsupportedMediaTypeException(_extension);
        }

        return chars;
    }

    private char[] Decode(ReadOnlySpan<byte> chunk)
    {
        try
        {
            var count = _decoder!.GetCharCount(chunk, flush: false);
            if (count == 0)
                return [];

            var chars = new char[count];
            var written = _decoder.GetChars(chunk, chars, flush: false);
            return written == chars.Length ? chars : chars[..written];
        }
        catch (DecoderFallbackException)
        {
            throw new UnsupportedMediaTypeException(_extension);
        }
    }

    private static bool HasDisallowedControl(ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not '\t' and not '\n' and not '\r')
                return true;
        }

        return false;
    }

    private static bool ContainsMarker(ReadOnlySpan<char> tail, ReadOnlySpan<char> chunk)
    {
        if (!chunk.IsEmpty && FileSignature.ContainsActiveContent(new string(chunk)))
            return true;

        if (tail.IsEmpty || chunk.IsEmpty)
            return false;

        var take = Math.Min(chunk.Length, TailChars + 1);
        var boundary = new char[tail.Length + take];
        tail.CopyTo(boundary);
        chunk[..take].CopyTo(boundary.AsSpan(tail.Length));
        return FileSignature.ContainsActiveContent(new string(boundary));
    }

    private void Remember(ReadOnlySpan<char> text)
    {
        if (text.Length >= TailChars)
        {
            text[^TailChars..].CopyTo(_tail);
            _tailLength = TailChars;
            return;
        }

        var combined = _tailLength + text.Length;
        if (combined <= TailChars)
        {
            text.CopyTo(_tail.AsSpan(_tailLength));
            _tailLength = combined;
            return;
        }

        var drop = combined - TailChars;
        _tail.AsSpan(drop, _tailLength - drop).CopyTo(_tail);
        text.CopyTo(_tail.AsSpan(_tailLength - drop));
        _tailLength = TailChars;
    }

    private static bool IsStrictUtf8(ReadOnlySpan<byte> header)
    {
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(header);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>Reads the inner stream and checks each chunk before the caller sees it.</summary>
internal sealed class ScanningReadStream : Stream
{
    private readonly Stream _inner;
    private readonly StreamingTextChecker _checker;

    public ScanningReadStream(Stream inner, StreamingTextChecker checker)
    {
        _inner = inner;
        _checker = checker;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
            _checker.Accept(buffer.Span[..read]);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
