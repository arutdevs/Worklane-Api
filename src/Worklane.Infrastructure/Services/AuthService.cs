using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Auth.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Options;
using Worklane.Application.Common.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly WorklaneDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly JwtSettings _jwtSettings;

    public AuthService(
        WorklaneDbContext db,
        UserManager<AppUser> userManager,
        ITokenService tokenService,
        IOptions<JwtSettings> jwtSettings)
    {
        _db = db;
        _userManager = userManager;
        _tokenService = tokenService;
        _jwtSettings = jwtSettings.Value;
    }

    public async Task<AuthTokensResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName,
            PreferredLanguage = "en"
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            throw new ConflictException(string.Join(" ", createResult.Errors.Select(e => e.Description)));
        }

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = $"{request.DisplayName}'s Workspace",
            Slug = await GenerateUniqueSlugAsync($"{request.DisplayName}-workspace", cancellationToken),
            Plan = WorkspacePlan.Free,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Workspaces.Add(workspace);

        _db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);

        return await IssueTokensAsync(user, cancellationToken);
    }

    public async Task<AuthTokensResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            throw new AuthenticationFailedException();
        }

        return await IssueTokensAsync(user, cancellationToken);
    }

    public async Task<AuthTokensResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = _tokenService.HashRefreshToken(refreshToken);
        var existing = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is null || !existing.IsActive)
        {
            throw new AuthenticationFailedException("Refresh token is invalid or expired.");
        }

        var user = await _userManager.FindByIdAsync(existing.UserId.ToString());
        if (user is null)
        {
            throw new AuthenticationFailedException();
        }

        existing.RevokedAt = DateTimeOffset.UtcNow;

        var result = await IssueTokensAsync(user, cancellationToken, skipSave: true);

        var newToken = await _db.RefreshTokens.OrderByDescending(t => t.CreatedAt).FirstAsync(t => t.UserId == user.Id, cancellationToken);
        existing.ReplacedByTokenId = newToken.Id;

        await _db.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = _tokenService.HashRefreshToken(refreshToken);
        var existing = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is not null && existing.IsActive)
        {
            existing.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<AuthTokensResponse> IssueTokensAsync(AppUser user, CancellationToken cancellationToken, bool skipSave = false)
    {
        var accessToken = _tokenService.CreateAccessToken(user.Id, user.Email!, user.DisplayName);
        var rawRefreshToken = _tokenService.GenerateRefreshTokenValue();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = _tokenService.HashRefreshToken(rawRefreshToken),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtSettings.RefreshTokenExpiryDays)
        });

        if (!skipSave)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        var userDto = new UserDto(user.Id, user.Email!, user.DisplayName, user.AvatarUrl, user.PreferredLanguage);
        return new AuthTokensResponse(
            accessToken,
            rawRefreshToken,
            DateTimeOffset.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpiryMinutes),
            userDto);
    }

    private async Task<string> GenerateUniqueSlugAsync(string baseName, CancellationToken cancellationToken)
    {
        var slug = Slugify(baseName);
        var candidate = slug;
        var suffix = 0;

        while (await _db.Workspaces.AnyAsync(w => w.Slug == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{slug}-{suffix}";
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        var lowered = value.ToLowerInvariant();
        var chars = lowered.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--"))
        {
            collapsed = collapsed.Replace("--", "-");
        }
        return collapsed.Trim('-');
    }
}
