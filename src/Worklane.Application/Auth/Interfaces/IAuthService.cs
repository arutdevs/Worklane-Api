using Worklane.Application.Auth.Dtos;

namespace Worklane.Application.Auth.Interfaces;

public interface IAuthService
{
    Task<AuthTokensResponse> RegisterAsync(RegisterRequest request, string? acceptLanguage, CancellationToken cancellationToken);
    Task<AuthTokensResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<AuthTokensResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken);
}
