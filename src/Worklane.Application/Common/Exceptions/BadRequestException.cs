using System.Net;

namespace Worklane.Application.Common.Exceptions;

public class BadRequestException : AppException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.BadRequest;

    public BadRequestException(string message) : base("bad_request", message) { }

    public BadRequestException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(code, message, parameters) { }
}
