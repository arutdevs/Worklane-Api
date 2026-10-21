using System.Net;

namespace Worklane.Application.Common.Exceptions;

public class UnsupportedMediaTypeException : AppException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.UnsupportedMediaType;

    public UnsupportedMediaTypeException(string? extension)
        : base(
            "unsupported_file_type",
            "The file type is not allowed.",
            ErrorParams.Of(("extension", extension)))
    {
        Extension = extension;
    }

    /// <summary>The filename extension the caller sent, including the leading dot.</summary>
    public string? Extension { get; }
}
