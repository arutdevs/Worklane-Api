namespace Worklane.Application.Common.Exceptions;

public class ForbiddenAccessException : AppException
{
    public ForbiddenAccessException(string message = "You do not have permission to perform this action.")
        : base("forbidden", message) { }

    public ForbiddenAccessException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(code, message, parameters) { }
}
