namespace Worklane.Application.Common.Exceptions;

/// <summary>
/// An error the API renders as an RFC 7807 problem. <see cref="Code"/> is the stable
/// snake_case value the Web app translates. <see cref="Params"/> are copied onto the
/// problem document as top-level fields (for example <c>activeTaskCount</c>).
/// </summary>
public abstract class AppException : Exception
{
    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>();

    protected AppException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(message)
    {
        Code = code;
        Params = parameters is { Count: > 0 } ? parameters : Empty;
    }

    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Params { get; }
}
