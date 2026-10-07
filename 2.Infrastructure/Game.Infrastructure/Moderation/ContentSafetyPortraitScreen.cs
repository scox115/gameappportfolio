using Azure;
using Azure.AI.ContentSafety;
using Game.Core.Interfaces;
using Game.Core.Moderation;
using Microsoft.Extensions.Logging;

namespace Game.Infrastructure.Moderation;

/// <summary>
/// Screens portraits with Azure AI Content Safety. The API signs in with its managed identity (the
/// deployment grants it "Cognitive Services User" on the resource), so there is no key anywhere.
/// </summary>
public class ContentSafetyPortraitScreen(ContentSafetyClient client, ILogger<ContentSafetyPortraitScreen> logger) : IPortraitScreen
{
    private static readonly Dictionary<ImageCategory, PortraitHarm> Harms = new()
    {
        [ImageCategory.Hate] = PortraitHarm.Hate,
        [ImageCategory.SelfHarm] = PortraitHarm.SelfHarm,
        [ImageCategory.Sexual] = PortraitHarm.Sexual,
        [ImageCategory.Violence] = PortraitHarm.Violence,
    };

    public async Task<PortraitScreening> ScreenAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new AnalyzeImageOptions(new ContentSafetyImageData(BinaryData.FromBytes(image)));
            var result = await client.AnalyzeImageAsync(options, cancellationToken);

            var severities = result.Value.CategoriesAnalysis
                .Where(c => Harms.ContainsKey(c.Category))
                .ToDictionary(c => Harms[c.Category], c => c.Severity ?? 0);
            return PortraitScreening.Judge(severities);
        }
        catch (RequestFailedException ex) when (ex.Status == 400)
        {
            // Too small, too large in pixels, or not really an image, though it starts like one.
            logger.LogInformation("Content Safety couldn't read a portrait: {Error}", ex.ErrorCode ?? ex.Message);
            return PortraitScreening.Unreadable;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Down, unreachable, out of its monthly allowance (429), or not allowed in yet (403). An unchecked portrait is never shown.
            logger.LogWarning(ex, "Content Safety didn't answer, so a portrait couldn't be checked.");
            return PortraitScreening.Unavailable;
        }
    }
}
