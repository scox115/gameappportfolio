using Game.Core.Media;

namespace Game.Core.Tests.Media;

public class AvatarImageTests
{
    [Fact]
    public void PngSignature_IsPng() =>
        Assert.Equal(AvatarImage.Png, AvatarImage.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]));

    [Fact]
    public void JpegSignature_IsJpeg() =>
        Assert.Equal(AvatarImage.Jpeg, AvatarImage.Detect([0xFF, 0xD8, 0xFF, 0xE1]));

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]               // cut-off PNG header
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })]               // "<svg"
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]   // "GIF89a"
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0 })]   // "RIFF" (WebP)
    public void AnythingElse_IsRejected(byte[] content) => Assert.Null(AvatarImage.Detect(content));
}
