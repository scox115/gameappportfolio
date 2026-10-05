using Game.Core.Entities;

namespace Game.Core.Tests.Entities;

public class PlayerTests
{
    [Fact]
    public void NewPlayer_StartsAtLevelOneWithStartingGold()
    {
        var player = new Player("hero", 500);

        Assert.Equal(500, player.Gold);
        Assert.Equal(1, player.Level);
        Assert.Equal(0, player.ExperiencePoints);
    }

    [Fact]
    public void NewPlayer_CanShareItsIdWithASignInAccount()
    {
        var accountId = Guid.NewGuid();
        var player = new Player(accountId, "hero", 500);

        Assert.Equal(accountId, player.Id);
    }

    [Fact]
    public void NewPlayer_RejectsEmptyId()
    {
        Assert.Throws<ArgumentException>(() => new Player(Guid.Empty, "hero", 500));
    }

    [Fact]
    public void AddGold_RejectsNegativeAmounts()
    {
        var player = new Player("hero", 500);
        Assert.Throws<ArgumentException>(() => player.AddGold(-1));
    }

    [Fact]
    public void DeductGold_RemovesGold()
    {
        var player = new Player("hero", 500);
        player.DeductGold(200);
        Assert.Equal(300, player.Gold);
    }

    [Fact]
    public void DeductGold_ThrowsWhenBalanceTooLow()
    {
        var player = new Player("hero", 50);

        Assert.Throws<InvalidOperationException>(() => player.DeductGold(51));
        Assert.Equal(50, player.Gold);
    }

    [Fact]
    public void AddExperience_LevelsUpAtThreshold()
    {
        var player = new Player("hero", 0);

        player.AddExperience(100);

        Assert.Equal(2, player.Level);
        Assert.Equal(0, player.ExperiencePoints);
    }

    [Fact]
    public void AddExperience_CarriesOverflowIntoNextLevel()
    {
        var player = new Player("hero", 0);

        player.AddExperience(130);

        Assert.Equal(2, player.Level);
        Assert.Equal(30, player.ExperiencePoints);
    }

    [Fact]
    public void AddExperience_IgnoresNegativeAmounts()
    {
        var player = new Player("hero", 0);

        player.AddExperience(-10);

        Assert.Equal(0, player.ExperiencePoints);
    }
}
