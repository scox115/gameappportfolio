using System.Text;
using Game.Core.Security;

namespace Game.Core.Tests.Security;

public class TotpTests
{
    // The SHA-1 test key from RFC 6238 appendix B.
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    // RFC 6238's eight-digit answers, cut to the last six digits authenticator apps show.
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Codes_MatchTheRfcTestVectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, Totp.Code(Key, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));
    }

    [Fact]
    public void ACode_IsAcceptedForOneStepEitherSide_ToAllowForClockDrift()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Match(Key, Totp.Code(Key, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Match(Key, Totp.Code(Key, step + 1), now, null));
        Assert.Null(Totp.Match(Key, Totp.Code(Key, step - 2), now, null));
        Assert.Null(Totp.Match(Key, Totp.Code(Key, step + 2), now, null));
    }

    [Fact]
    public void ACodeThatWasUsed_OrAnOlderOne_IsRefused()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var step = Totp.StepAt(now);

        Assert.Null(Totp.Match(Key, Totp.Code(Key, step), now, lastUsedStep: step));
        Assert.Null(Totp.Match(Key, Totp.Code(Key, step - 1), now, lastUsedStep: step));
        Assert.Equal(step + 1, Totp.Match(Key, Totp.Code(Key, step + 1), now, lastUsedStep: step));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void Malformed_CodesAreRefused(string? code)
    {
        Assert.Null(Totp.Match(Key, code, DateTimeOffset.UnixEpoch, null));
    }

    [Fact]
    public void SpacesAndDashes_InACodeAreIgnored()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.NotNull(Totp.Match(Key, "287 082", now, null));
        Assert.NotNull(Totp.Match(Key, "287-082", now, null));
    }

    [Fact]
    public void Base32_RoundTrips_AndIgnoresSpacingAndCase()
    {
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)(i * 13)).ToArray();
        var text = Base32.Encode(bytes);

        Assert.Equal(bytes, Base32.Decode(text));
        Assert.Equal(bytes, Base32.Decode(string.Join(' ', text.Chunk(4).Select(c => new string(c))).ToLowerInvariant()));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(Key)); // RFC 6238's key as apps see it
        Assert.Throws<FormatException>(() => Base32.Decode("ABC1"));
    }
}
