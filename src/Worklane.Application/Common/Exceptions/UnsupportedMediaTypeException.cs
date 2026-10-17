using System.Net;

namespace Worklane.Application.Common.Exceptions;

public class UnsupportedMediaTypeException : AppException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.UnsupportedMediaType;

    public UnsupportedMediaTypeException(string? contentType)
        : base("unsupported_media_type", $"Content type \"{contentType ?? "(none)"}\" is not allowed.")
    {
        ContentType = contentType;
    }

    public string? ContentType { get; }
}
