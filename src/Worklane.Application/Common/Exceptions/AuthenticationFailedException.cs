namespace Worklane.Application.Common.Exceptions;

public class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException(string message = "Invalid credentials.") : base(message) { }
}
