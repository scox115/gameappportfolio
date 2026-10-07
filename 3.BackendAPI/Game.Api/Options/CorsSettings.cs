using System.Text.RegularExpressions;

namespace Game.Api.Options;

// Bound from the "Cors" configuration section.
public class CorsSettings
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// A Static Web App's default host name (such as <c>gray-coast-028faf70f.4.azurestaticapps.net</c>)
    /// whose pull request previews may call the API too. Each preview gets its own address, such as
    /// <c>gray-coast-028faf70f-42.eastus2.4.azurestaticapps.net</c> for pull request 42, so they can't be
    /// listed ahead of time. Set only in staging (see docs/adr/0026-staging-and-previews.md).
    /// </summary>
    public string? PreviewsOf { get; set; }

    public bool IsAllowed(string origin)
    {
        if (AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
        return PreviewPattern() is { } pattern && pattern.IsMatch(origin);
    }

    private Regex? _previewPattern;

    private Regex? PreviewPattern()
    {
        if (_previewPattern is not null || string.IsNullOrWhiteSpace(PreviewsOf)) return _previewPattern;

        // "name.4.azurestaticapps.net" -> previews at "name-<number>.<region>.4.azurestaticapps.net" (older apps
        // have no partition number). The name is unique to this app, so only its previews match.
        var name = PreviewsOf.Trim().ToLowerInvariant().Split('.')[0];
        if (name.Length == 0 || !PreviewsOf.Trim().EndsWith(".azurestaticapps.net", StringComparison.OrdinalIgnoreCase)) return null;
        return _previewPattern = new Regex(
            $@"^https://{Regex.Escape(name)}-\d{{1,6}}\.[a-z0-9]+(\.\d+)?\.azurestaticapps\.net$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    }
}
