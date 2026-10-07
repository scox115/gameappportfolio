namespace Game.Api.Options;

// Bound from the "ContentSafety" configuration section; see docs/adr/0029-portrait-screening.md.
public class ContentSafetyOptions
{
    public const string SectionName = "ContentSafety";

    /// <summary>
    /// The Azure AI Content Safety endpoint, such as https://cs-cardarena-abc.cognitiveservices.azure.com/.
    /// Empty: portraits aren't screened, and players' reports are the only check.
    /// </summary>
    public string? Endpoint { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);
}
