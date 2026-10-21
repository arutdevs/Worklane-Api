using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
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
    internal const int MaxSlugInsertAttempts = 8;
    internal const string SlugUnavailableCode = "workspace_slug_unavailable";
    internal const string SlugUnavailableDetail = "A unique workspace address could not be reserved. Try again.";

    private const string AccountExistsDetail = "An account with this email already exists.";

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

    public async Task<AuthTokensResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        // A retrying strategy rejects a user transaction unless the whole unit runs inside Execute.
        var strategy = _db.Database.CreateExecutionStrategy();
        if (strategy.RetriesOnFailure)
            return await strategy.ExecuteAsync(ct => RegisterNewUserAsync(request, ct), cancellationToken);

        return await RegisterNewUserAsync(request, cancellationToken);
    }

    private async Task<AuthTokensResponse> RegisterNewUserAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        // A strategy retry must not treat a rolled-back user still sitting in the tracker as a duplicate.
        _db.ChangeTracker.Clear();

        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            throw new ConflictException(AccountExistsDetail);

        var baseSlug = WorkspaceSlug.ForDisplayName(request.DisplayName);
        var minimumSuffix = 0;

        for (var attempt = 0; attempt < MaxSlugInsertAttempts; attempt++)
        {
            _db.ChangeTracker.Clear();
            var attemptedSuffix = minimumSuffix;
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var user = new AppUser
                {
                    UserName = request.Email,
                    Email = request.Email,
                    DisplayName = request.DisplayName,
                    PreferredLanguage = "en"
                };

                var createResult = await _userManager.CreateAsync(user, request.Password);
                if (!createResult.Succeeded)
                    throw new ConflictException(string.Join(" ", createResult.Errors.Select(e => e.Description)));

                var slug = await WorkspaceSlug.GenerateUniqueAsync(
                    _db.Workspaces,
                    baseSlug,
                    cancellationToken,
                    minimumSuffix);
                attemptedSuffix = slug.Suffix;

                var workspace = new Workspace
                {
                    Id = Guid.NewGuid(),
                    Name = $"{request.DisplayName}'s Workspace",
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

                await _db.SaveChangesAsync(cancellationToken);
                var tokens = await IssueTokensAsync(user, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return tokens;
            }
            catch (Exception ex) when (IsUniqueViolation(ex, WorkspaceSlug.UniqueIndexName))
            {
                minimumSuffix = attemptedSuffix + 1;
            }
            catch (Exception ex) when (IsUniqueViolation(ex, "EmailIndex") || IsUniqueViolation(ex, "UserNameIndex"))
            {
                throw new ConflictException(AccountExistsDetail);
            }
        }

        throw new ConflictException(SlugUnavailableCode, SlugUnavailableDetail);
    }

    private static bool IsUniqueViolation(Exception exception, string constraintName)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
