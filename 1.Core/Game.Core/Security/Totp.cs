using System.Security.Cryptography;

namespace Game.Core.Security;

/// <summary>
/// Time-based one-time codes (RFC 6238), the six-digit codes authenticator apps such as Google
/// Authenticator, Microsoft Authenticator and 1Password show. A code changes every 30 seconds.
/// </summary>
/// <remarks>
/// ASP.NET Core Identity has its own checker, but it accepts the same code again for as long as it
/// is valid and reads the system clock. This one reports which 30-second step matched, so a code
/// that has been used once is refused (RFC 6238 section 5.2), and it takes the time as an argument.
/// </remarks>
public static class Totp
{
    public const int Digits = 6;
    public static readonly TimeSpan StepLength = TimeSpan.FromSeconds(30);

    /// <summary>How many steps either side of now are still accepted, for clocks that are a little off.</summary>
    public const int AllowedDrift = 1;

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)StepLength.TotalSeconds;

    /// <summary>The code an authenticator app shows for <paramref name="key"/> during <paramref name="step"/>.</summary>
    public static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        // Dynamic truncation (RFC 4226 section 5.3).
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// The step <paramref name="code"/> belongs to, or null when it is wrong, too old, or no newer than
    /// <paramref name="lastUsedStep"/> (it, or a later code, was used already).
    /// </summary>
    public static long? Match(byte[] key, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        code = code?.Replace(" ", "").Replace("-", "");
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit)) return null;

        var current = StepAt(now);
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            if (step > (lastUsedStep ?? long.MinValue) &&
                CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Code(key, step)), System.Text.Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }
        return null;
    }
}
