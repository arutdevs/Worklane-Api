using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Auth.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class AuthService : IAuthService
{
    internal const int MaxSlugInsertAttempts = WorkspaceSlug.MaxInsertAttempts;

    private readonly WorklaneDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly JwtSettings _jwtSettings;

    public AuthService(
        WorklaneDbContext db,
        UserManager<AppUser> userManager,
        IUserStore<AppUser> userStore,
        ITokenService tokenService,
        IOptions<JwtSettings> jwtSettings)
    {
        // Identity's EF store type grew extra parameters (passkeys). The context property
        // is what CreateAsync saves through, so registration and the store must share it.
        var storeContext = userStore.GetType().GetProperty("Context")?.GetValue(userStore);
        if (!ReferenceEquals(storeContext, db))
            throw new InvalidOperationException("Identity UserManager must use the same DbContext as registration.");

        _db = db;
        _userManager = userManager;
        _tokenService = tokenService;
        _jwtSettings = jwtSettings.Value;
    }

    public Task<AuthTokensResponse> RegisterAsync(
        RegisterRequest request,
        string? acceptLanguage,
        CancellationToken cancellationToken)
    {
        // A retrying strategy rejects a user transaction unless the whole unit runs inside Execute.
        return DbRetry.ExecuteAsync(_db, ct => RegisterNewUserAsync(request, acceptLanguage, ct), cancellationToken);
    }

    private async Task<AuthTokensResponse> RegisterNewUserAsync(
        RegisterRequest request,
        string? acceptLanguage,
        CancellationToken cancellationToken)
    {
        // A strategy retry must not treat a rolled-back user still sitting in the tracker as a duplicate.
        _db.ChangeTracker.Clear();

        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            throw UniqueConstraintConflicts.EmailAlreadyRegistered();

        var language = WorkspaceLocale.Resolve(request.PreferredLanguage, acceptLanguage);
        var baseSlug = WorkspaceSlug.ForDisplayName(request.DisplayName);

        return await WorkspaceSlug.InsertAsync(_db, baseSlug, async (slug, ct) =>
        {
            var user = new AppUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = request.DisplayName,
                PreferredLanguage = language
            };

            IdentityResult createResult;
            try
            {
                createResult = await _userManager.CreateAsync(user, request.Password);
            }
            catch (Exception ex) when (UniqueConstraintConflicts.IsAccountConstraint(ex))
            {
                throw UniqueConstraintConflicts.EmailAlreadyRegistered();
            }

            if (!createResult.Succeeded)
            {
                if (createResult.Errors.Any(static e => e.Code is "DuplicateEmail" or "DuplicateUserName"))
                    throw UniqueConstraintConflicts.EmailAlreadyRegistered();

                throw new ConflictException(string.Join(" ", createResult.Errors.Select(e => e.Description)));
            }

            var workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = WorkspaceLocale.WorkspaceName(request.DisplayName, language),
                Slug = slug.Value,
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

            await _db.SaveChangesAsync(ct);
            return await IssueTokensAsync(user, ct);
        }, cancellationToken);
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
}
