namespace Worklane.Application.Common.Exceptions;

public class NotFoundException : AppException
{
    public NotFoundException(string message) : base("not_found", message) { }

    public NotFoundException(string entityName, object key)
        : base("not_found", $"Entity \"{entityName}\" ({key}) was not found.") { }

    public NotFoundException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(code, message, parameters) { }
}
