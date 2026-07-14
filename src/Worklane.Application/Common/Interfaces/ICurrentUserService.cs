namespace Worklane.Application.Common.Interfaces;

public interface ICurrentUserService
{
    Guid UserId { get; }
    string? Email { get; }
}
