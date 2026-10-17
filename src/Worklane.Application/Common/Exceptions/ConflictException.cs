using System.Net;

namespace Worklane.Application.Common.Exceptions;

public class ConflictException : AppException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.Conflict;

    public ConflictException(string message) : base("conflict", message) { }

    public ConflictException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(code, message, parameters) { }
}
