namespace Worklane.Application.Common.Exceptions;

public class ConflictException : AppException
{
    public ConflictException(string message) : base("conflict", message) { }

    public ConflictException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(code, message, parameters) { }
}
