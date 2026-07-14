namespace Worklane.Application.Common.Interfaces;

public interface ITokenService
{
    string CreateAccessToken(Guid userId, string email, string displayName);
    string GenerateRefreshTokenValue();
    string HashRefreshToken(string rawToken);
}
