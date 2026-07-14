namespace Worklane.Application.Auth.Dtos;

public record RegisterRequest(string Email, string Password, string DisplayName);

public record LoginRequest(string Email, string Password);

public record RefreshRequest(string RefreshToken);

public record LogoutRequest(string RefreshToken);

public record UserDto(Guid Id, string Email, string DisplayName, string? AvatarUrl, string PreferredLanguage);

public record AuthTokensResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);
