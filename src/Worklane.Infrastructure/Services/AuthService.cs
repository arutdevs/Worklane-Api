using System.Reflection;
using System.Security.Cryptography;
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

    private static readonly AppUser DummyHashUser = new();

    // One Identity hash of a random password, computed once per process. Unknown and locked
    // logins verify against it so those paths do the same PBKDF2 work as a real check.
    private static readonly string DummyPasswordHash = new PasswordHasher<AppUser>().HashPassword(
        DummyHashUser,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private readonly WorklaneDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly JwtSettings _jwtSettings;
    private readonly IdentityOptions _identityOptions;

    public AuthService(
        WorklaneDbContext db,
        UserManager<AppUser> userManager,
        IUserStore<AppUser> userStore,
        ITokenService tokenService,
        IOptions<JwtSettings> jwtSettings,
        IOptions<IdentityOptions> identityOptions)
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
        _identityOptions = identityOptions.Value;
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
        if (user is null)
        {
            VerifyDummyPassword(request.Password);
            throw new AuthenticationFailedException();
        }

        if (!_userManager.SupportsUserLockout)
        {
            if (!await _userManager.CheckPasswordAsync(user, request.Password))
                throw new AuthenticationFailedException();

            return await IssueTokensAsync(user, cancellationToken);
        }

        // Rows created outside UserManager can still have LockoutEnabled false. Identity then
        // ignores LockoutEnd. Flip it on before the lockout check. One UPDATE, no migration.
        await _db.Users
            .Where(account => account.Id == user.Id && !account.LockoutEnabled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.LockoutEnabled, true), cancellationToken);
        await _db.Entry(user).ReloadAsync(cancellationToken);

        if (await _userManager.IsLockedOutAsync(user))
        {
            VerifyDummyPassword(request.Password);
            throw new AuthenticationFailedException();
        }

        // AccessFailedAsync updates the whole row and drops the write when two requests
        // race on the concurrency stamp. Claim the attempt in one conditional UPDATE so
        // parallel guesses cannot pass the configured limit.
        var maxAttempts = _identityOptions.Lockout.MaxFailedAccessAttempts;
        var now = DateTimeOffset.UtcNow;
        var claimed = await _db.Users
            .Where(account => account.Id == user.Id
                && (account.LockoutEnd == null || account.LockoutEnd < now)
                && account.AccessFailedCount < maxAttempts)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(account => account.AccessFailedCount, account => account.AccessFailedCount + 1),
                cancellationToken);

        if (claimed == 0)
        {
            await LockOutIfThresholdReachedAsync(user.Id, maxAttempts, cancellationToken);
            VerifyDummyPassword(request.Password);
            throw new AuthenticationFailedException();
        }

        await _db.Entry(user).ReloadAsync(cancellationToken);
        if (!await _userManager.CheckPasswordAsync(user, request.Password))
        {
            if (user.AccessFailedCount >= maxAttempts)
                await LockOutIfThresholdReachedAsync(user.Id, maxAttempts, cancellationToken);

            throw new AuthenticationFailedException();
        }

        await _db.Users
            .Where(account => account.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(account => account.AccessFailedCount, 0)
                .SetProperty(account => account.LockoutEnd, (DateTimeOffset?)null), cancellationToken);

        return await IssueTokensAsync(user, cancellationToken);
    }

    private void VerifyDummyPassword(string password) =>
        _userManager.PasswordHasher.VerifyHashedPassword(DummyHashUser, DummyPasswordHash, password);

    private async Task LockOutIfThresholdReachedAsync(Guid userId, int maxAttempts, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow.Add(_identityOptions.Lockout.DefaultLockoutTimeSpan);
        await _db.Users
            .Where(account => account.Id == userId && account.AccessFailedCount >= maxAttempts)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(account => account.LockoutEnd, until)
                .SetProperty(account => account.AccessFailedCount, 0), cancellationToken);
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
