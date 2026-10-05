using Game.Core.Entities;

namespace Game.Core.Tests.Entities;

public class GameMatchTests
{
    [Fact]
    public void CreatePve_UsesAiBossAsOpponent()
    {
        var playerId = Guid.NewGuid();

        var match = GameMatch.CreatePve(playerId);

        Assert.Equal(playerId, match.PlayerOneId);
        Assert.Equal(GameMatch.AiBossId, match.PlayerTwoId);
        Assert.True(match.IsPve);
        Assert.False(match.IsCompleted);
    }

    [Fact]
    public void Constructor_RejectsSelfMatch()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new GameMatch(id, id));
    }

    [Fact]
    public void Constructor_RejectsEmptyIds()
    {
        Assert.Throws<ArgumentException>(() => new GameMatch(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new GameMatch(Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public void CompleteMatch_RejectsNonParticipantWinner()
    {
        var match = new GameMatch(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => match.CompleteMatch(Guid.NewGuid()));
    }

    [Fact]
    public void CompleteMatch_CannotRunTwice()
    {
        var p1 = Guid.NewGuid();
        var match = new GameMatch(p1, Guid.NewGuid());
        match.CompleteMatch(p1);

        Assert.Throws<InvalidOperationException>(() => match.CompleteMatch(p1));
    }
}
