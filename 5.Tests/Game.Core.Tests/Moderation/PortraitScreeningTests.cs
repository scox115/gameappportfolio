using Game.Core.Moderation;

namespace Game.Core.Tests.Moderation;

public class PortraitScreeningTests
{
    private static PortraitScreening Judge(params (PortraitHarm Harm, int Severity)[] severities) =>
        PortraitScreening.Judge(severities.ToDictionary(s => s.Harm, s => s.Severity));

    [Fact]
    public void ASafeImage_IsAllowed()
    {
        var screening = Judge((PortraitHarm.Hate, 0), (PortraitHarm.SelfHarm, 0), (PortraitHarm.Sexual, 0), (PortraitHarm.Violence, 0));

        Assert.Equal(PortraitVerdict.Allowed, screening.Verdict);
        Assert.Null(screening.Explanation);
    }

    [Theory]
    [InlineData(PortraitHarm.Hate)]
    [InlineData(PortraitHarm.SelfHarm)]
    [InlineData(PortraitHarm.Sexual)]
    public void HateSexOrSelfHarm_IsBlockedFromLowSeverity(PortraitHarm harm)
    {
        Assert.Equal(new PortraitScreening(PortraitVerdict.Blocked, harm), Judge((harm, 2)));
    }

    [Fact]
    public void FantasyViolence_IsAllowed_ButGraphicViolenceIsNot()
    {
        // A hero holding a sword can rate as low violence; this is a battle game.
        Assert.Equal(PortraitVerdict.Allowed, Judge((PortraitHarm.Violence, 2)).Verdict);
        Assert.Equal(new PortraitScreening(PortraitVerdict.Blocked, PortraitHarm.Violence), Judge((PortraitHarm.Violence, 4)));
    }

    [Fact]
    public void TheWorstHarmFound_IsTheReasonGiven()
    {
        var screening = Judge((PortraitHarm.Hate, 2), (PortraitHarm.Violence, 6));

        Assert.Equal(PortraitHarm.Violence, screening.Harm);
        Assert.Contains("violent", screening.Explanation);
    }

    [Fact]
    public void EveryTurnedAwayImage_ExplainsWhy()
    {
        foreach (var harm in Enum.GetValues<PortraitHarm>())
        {
            Assert.StartsWith("This portrait can't be used", new PortraitScreening(PortraitVerdict.Blocked, harm).Explanation);
        }
        Assert.Contains("50 × 50", PortraitScreening.Unreadable.Explanation);
        Assert.Contains("try again", PortraitScreening.Unavailable.Explanation);
    }
}
