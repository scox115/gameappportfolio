using Game.Api.Hubs;

namespace Game.Api.Tests;

public class MatchmakerTests
{
    [Fact]
    public void PlayersOnTheSameNetwork_ArePairedForAFriendlyDuel_ButFlagged()
    {
        var matchmaker = new PvpMatchmaker();
        var alice = Guid.NewGuid();

        Assert.Null(matchmaker.JoinOrPair(alice, 0, "203.0.113.7"));
        var pairing = matchmaker.JoinOrPair(Guid.NewGuid(), 0, "203.0.113.7");

        Assert.Equal(new PvpPairing(alice, SameNetwork: true), pairing);
    }

    [Fact]
    public void AWagerSkipsOpponentsOnTheSameNetwork()
    {
        var matchmaker = new PvpMatchmaker();
        var alice = Guid.NewGuid();
        var carol = Guid.NewGuid();
        matchmaker.JoinOrPair(alice, 100, "203.0.113.7", avoidSameNetwork: true);

        Assert.Null(matchmaker.JoinOrPair(Guid.NewGuid(), 100, "203.0.113.7", avoidSameNetwork: true));
        var pairing = matchmaker.JoinOrPair(carol, 100, "198.51.100.9", avoidSameNetwork: true);

        Assert.Equal(new PvpPairing(alice, SameNetwork: false), pairing);
    }

    [Fact]
    public void AnUnknownAddress_IsNeverTreatedAsTheSameNetwork()
    {
        var matchmaker = new PvpMatchmaker();
        matchmaker.JoinOrPair(Guid.NewGuid(), 0, network: null);

        Assert.False(matchmaker.JoinOrPair(Guid.NewGuid(), 0, network: null)!.SameNetwork);
    }
}
