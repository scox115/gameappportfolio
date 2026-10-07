using System.Globalization;
using System.Net;

namespace Game.Client.Services;

/// <summary>Where a hero's portrait comes from: their uploaded image, or else a badge with their initial.</summary>
public static class Avatars
{
    /// <summary>
    /// The uploaded portrait, or an SVG badge drawn in the browser. The badge needs no outside
    /// service, and its colour comes from the name, so each hero keeps the same one.
    /// </summary>
    public static string For(string? avatarUrl, string? username)
    {
        if (!string.IsNullOrEmpty(avatarUrl)) return avatarUrl;

        var name = string.IsNullOrWhiteSpace(username) ? "?" : username.Trim();
        // The first whole character, so a name starting with an emoji (the Arena Bot's) keeps it.
        var initial = WebUtility.HtmlEncode(StringInfo.GetNextTextElement(name).ToUpperInvariant());
        // A stable hash (string.GetHashCode changes between runs); dark enough for white text.
        var hue = (name.Aggregate(0, (hash, c) => unchecked(hash * 31 + c)) & 0x7fffffff) % 360;
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'><rect width='64' height='64' fill='hsl({hue},55%,35%)'/>" +
                  $"<text x='32' y='42' font-family='sans-serif' font-size='30' font-weight='bold' fill='white' text-anchor='middle'>{initial}</text></svg>";
        return "data:image/svg+xml;utf8," + Uri.EscapeDataString(svg);
    }
}
