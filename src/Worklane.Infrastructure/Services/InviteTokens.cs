using System.Security.Cryptography;
using System.Text;

namespace Worklane.Infrastructure.Services;

internal static class InviteTokens
{
    public const int ByteLength = 32;

    public static string Generate() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(ByteLength));

    public static string Hash(string rawToken)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(digest);
    }
}
