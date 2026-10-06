namespace Game.Core.Media;

/// <summary>An image type players may use as a portrait.</summary>
public sealed record AvatarImageFormat(string ContentType, string Extension);

/// <summary>
/// Rules for uploaded portraits. The type comes from the file's first bytes, never from its name or
/// the browser's Content-Type, so a script or web page renamed to .png is turned away.
/// </summary>
public static class AvatarImage
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public static readonly AvatarImageFormat Png = new("image/png", ".png");
    public static readonly AvatarImageFormat Jpeg = new("image/jpeg", ".jpg");

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>Returns the image type the bytes start with, or null if it isn't one we accept.</summary>
    public static AvatarImageFormat? Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(PngSignature)) return Png;
        if (content.StartsWith(JpegSignature)) return Jpeg;
        return null;
    }
}
