using System.Net;

namespace Worklane.Application.Common.Exceptions;

public class AuthenticationFailedException : AppException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.Unauthorized;

    public AuthenticationFailedException(string message = "Invalid credentials.")
        : base("unauthorized", message) { }

    public AuthenticationFailedException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(code, message, parameters) { }
}
