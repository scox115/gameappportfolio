namespace Game.Core.Moderation;

/// <summary>The kinds of harm an uploaded portrait is screened for.</summary>
public enum PortraitHarm
{
    Hate,
    SelfHarm,
    Sexual,
    Violence
}

public enum PortraitVerdict
{
    /// <summary>Nothing harmful was found, or screening is switched off.</summary>
    Allowed,

    /// <summary>The image shows something players shouldn't see.</summary>
    Blocked,

    /// <summary>The screening service couldn't read the image, such as one smaller than 50 × 50 pixels.</summary>
    Unreadable,

    /// <summary>The screening service didn't answer, so the image wasn't checked.</summary>
    Unavailable
}

/// <param name="Harm">What the image was blocked for; null unless it was blocked.</param>
public sealed record PortraitScreening(PortraitVerdict Verdict, PortraitHarm? Harm = null)
{
    public static readonly PortraitScreening Allowed = new(PortraitVerdict.Allowed);
    public static readonly PortraitScreening Unreadable = new(PortraitVerdict.Unreadable);
    public static readonly PortraitScreening Unavailable = new(PortraitVerdict.Unavailable);

    /// <summary>
    /// Decides from the severity found for each kind of harm, on Azure AI Content Safety's image scale:
    /// 0 safe, 2 low, 4 medium, 6 high. See docs/adr/0029-portrait-screening.md.
    /// </summary>
    public static PortraitScreening Judge(IReadOnlyDictionary<PortraitHarm, int> severities)
    {
        var worst = severities
            .Where(s => s.Value >= BlockAt(s.Key))
            .OrderByDescending(s => s.Value)
            .Select(s => (PortraitHarm?)s.Key)
            .FirstOrDefault();
        return worst is { } harm ? new(PortraitVerdict.Blocked, harm) : Allowed;
    }

    /// <summary>
    /// The lowest severity that blocks a portrait. Anything hateful, sexual or about self-harm is
    /// blocked from "low" up. Violence only from "medium": this is a fantasy battle game, and a hero
    /// holding a sword or facing a dragon can rate as low violence.
    /// </summary>
    public static int BlockAt(PortraitHarm harm) => harm == PortraitHarm.Violence ? 4 : 2;

    /// <summary>What the player is told when their portrait is turned away.</summary>
    public string? Explanation => Verdict switch
    {
        PortraitVerdict.Blocked => Harm switch
        {
            PortraitHarm.Sexual => "This portrait can't be used: it looks like it contains sexual content.",
            PortraitHarm.Violence => "This portrait can't be used: it looks too graphically violent.",
            PortraitHarm.SelfHarm => "This portrait can't be used: it looks like it shows self-harm.",
            _ => "This portrait can't be used: it looks like it contains hateful content."
        },
        PortraitVerdict.Unreadable => "This image couldn't be checked. Use a PNG or JPG at least 50 × 50 pixels.",
        PortraitVerdict.Unavailable => "Portraits can't be checked right now, so they can't be changed. Please try again in a few minutes.",
        _ => null
    };
}
