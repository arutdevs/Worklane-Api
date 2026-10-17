namespace Worklane.Application.Common.Exceptions;

public class UnsupportedMediaTypeException : AppException
{
    public UnsupportedMediaTypeException(string? contentType)
        : base("unsupported_media_type", $"Content type \"{contentType ?? "(none)"}\" is not allowed.")
    {
        ContentType = contentType;
    }

    public string? ContentType { get; }
}
