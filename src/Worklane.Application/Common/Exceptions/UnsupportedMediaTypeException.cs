namespace Worklane.Application.Common.Exceptions;

public class UnsupportedMediaTypeException : Exception
{
    public UnsupportedMediaTypeException(string? contentType)
        : base($"Content type \"{contentType ?? "(none)"}\" is not allowed.")
    {
        ContentType = contentType;
    }

    public string? ContentType { get; }
}
