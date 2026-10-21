using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Worklane.Application.Common.Options;

namespace Worklane.Infrastructure.Identity;

public sealed class JwtSigningMaterial
{
    public required SymmetricSecurityKey Current { get; init; }
    public required IReadOnlyList<SymmetricSecurityKey> All { get; init; }
}

/// <summary>
/// Resolves the current signing key and any previous keys. Rejects a missing, short,
/// or known-leaked key in every environment. Comparison is the SHA-256 of the decoded
/// key bytes, so whitespace in the configured text does not hide a leaked key.
/// </summary>
public static class JwtSigningKeys
{
    public const int MinimumBytes = 32;

    // SHA-256 of the key bytes that used to be committed for local dev.
    // The plaintext is not stored. Tests may add a generated key's fingerprint here.
    internal static readonly HashSet<string> LeakedFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "6478a52cc5de53f75fc5b15ccda93afa869c601533c7b5c6894d963e0a839ff4"
    };

    public static string Fingerprint(ReadOnlySpan<byte> keyBytes) =>
        Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();

    public static JwtSigningMaterial Create(JwtSettings settings)
    {
        var current = ToKey(Decode(settings.Secret, "Jwt:Secret"));
        var all = new List<SymmetricSecurityKey> { current };
        var previous = settings.PreviousSecrets;
        if (previous is not null)
        {
            for (var i = 0; i < previous.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(previous[i]))
                    continue;

                var key = ToKey(Decode(previous[i], $"Jwt:PreviousSecrets[{i}]"));
                if (all.TrueForAll(existing => existing.KeyId != key.KeyId))
                    all.Add(key);
            }
        }

        return new JwtSigningMaterial { Current = current, All = all };
    }

    private static byte[] Decode(string? configured, string name)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{name} is missing. Set a base64-encoded key of at least {MinimumBytes} bytes.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{name} is not valid base64.");
        }

        if (key.Length < MinimumBytes)
        {
            throw new InvalidOperationException(
                $"{name} must decode to at least {MinimumBytes} bytes.");
        }

        var fingerprint = Fingerprint(key);
        if (LeakedFingerprints.Contains(fingerprint))
        {
            throw new InvalidOperationException(
                $"{name} matches a known leaked signing key (fingerprint {fingerprint}) and cannot be used.");
        }

        return key;
    }

    private static SymmetricSecurityKey ToKey(byte[] key) =>
        new(key) { KeyId = Fingerprint(key) };
}

public sealed class JwtSettingsValidator : IValidateOptions<JwtSettings>
{
    public ValidateOptionsResult Validate(string? name, JwtSettings options)
    {
        try
        {
            _ = JwtSigningKeys.Create(options);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
