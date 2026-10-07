using Game.Core.Moderation;

namespace Game.Core.Tests.Moderation;

public class HeroNamesTests
{
    [Theory]
    [InlineData("Ali")]
    [InlineData("Sir Lancelot")]
    [InlineData("Zoë_the-Brave")]
    [InlineData("O'Brien.2")]
    [InlineData("Dragon123")]
    // Harmless names that contain a blocked word.
    [InlineData("Cassandra")]
    [InlineData("Classic Knight")]
    [InlineData("Title Holder")]
    [InlineData("Peacock")]
    [InlineData("Dickens")]
    [InlineData("Titania")]
    [InlineData("The Therapist")]
    [InlineData("Swanky")]
    [InlineData("Torpedo")]
    [InlineData("Badminton Ace")]
    [InlineData("Sussex Knight")]
    public void FineNames_AreAllowed(string name)
    {
        Assert.Null(HeroNames.Problem(name));
    }

    [Theory]
    [InlineData("Fuck")]
    [InlineData("xXfuckXx")]
    [InlineData("F_u.c-k")]
    [InlineData("Fuuuuck")]
    [InlineData("5h1t lord")]
    [InlineData("SH1T")]
    [InlineData("Sh1iiit")]
    [InlineData("Big Ass")]
    [InlineData("Big_Ass")]
    [InlineData("BigAss")]
    [InlineData("Rapist")]
    [InlineData("Nazi Lord")]
    public void OffensiveNames_AreRefused(string name)
    {
        Assert.Equal("That name isn't allowed. Choose another.", HeroNames.Problem(name));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("AdminBob")]
    [InlineData("admin123")]
    [InlineData("Moderator_1")]
    [InlineData("Official Support")]
    [InlineData("The GM")]
    public void NamesThatPassForStaff_AreRefused(string name)
    {
        Assert.Contains("staff", HeroNames.Problem(name));
    }

    [Fact]
    public void TheGamesOwnAdmins_MayUseAStaffName_ButNotAnOffensiveOne()
    {
        Assert.Null(HeroNames.Problem("RefereeMod", allowStaffNames: true));
        Assert.NotNull(HeroNames.Problem("Admin Sh1t", allowStaffNames: true));
    }

    [Theory]
    [InlineData("ab", "3 to 50 characters")]
    [InlineData("<script>", "letters, numbers")]
    [InlineData("a​b​c", "letters, numbers")]
    [InlineData("12345", "at least one letter")]
    [InlineData("Two  Spaces", "two spaces")]
    [InlineData(" Padded", "start or end")]
    public void MalformedNames_AreRefusedWithAReason(string name, string expected)
    {
        Assert.Contains(expected, HeroNames.Problem(name));
    }

    [Fact]
    public void LongNames_AreRefused()
    {
        Assert.Null(HeroNames.Problem(new string('a', HeroNames.MaxLength)));
        Assert.NotNull(HeroNames.Problem(new string('a', HeroNames.MaxLength + 1)));
    }
}
