using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Game.Api.Auth;

/// <summary>Random tokens that are handed out once and stored only as a hash.</summary>
public static class SecureTokens
{
    /// <summary>A new random token, safe to put in a URL.</summary>
    public static string New(int bytes = 32) => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>Hex-encoded SHA-256, which is what the database keeps.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
