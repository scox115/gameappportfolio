using Game.Core.Admin;
using Game.Core.Entities;

namespace Game.Core.Tests.Admin;

public class AuditLogEntryTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void AnEntry_RecordsWhoDidWhatToWhomAndWhy()
    {
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();

        var entry = new AuditLogEntry(Now, AdminAction.Suspend, actor, "Scottie", target, "cheater", "  Used a bot in duels.  ", "For 7 days.");

        Assert.Equal((Now, AdminAction.Suspend, actor, "Scottie"), (entry.At, entry.Action, entry.ActorId, entry.ActorName));
        Assert.Equal((target, "cheater"), (entry.TargetId, entry.TargetName));
        Assert.Equal("Used a bot in duels.", entry.Reason);
        Assert.Equal("For 7 days.", entry.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEntry_NeedsAReason(string? reason)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new AuditLogEntry(Now, AdminAction.AdjustGold, Guid.NewGuid(), "Scottie", Guid.NewGuid(), "alice", reason!));

        Assert.StartsWith("Give a reason", error.Message);
    }

    [Fact]
    public void AReason_CanBeAtMost500Characters()
    {
        Assert.Equal(500, AuditLogEntry.RequireReason(new string('x', 500)).Length);
        Assert.Throws<ArgumentException>(() => AuditLogEntry.RequireReason(new string('x', 501)));
    }

    [Fact]
    public void Times_AreKeptInUtc()
    {
        Assert.Throws<ArgumentException>(() =>
            new AuditLogEntry(DateTime.Now, AdminAction.Reinstate, Guid.NewGuid(), "Scottie", Guid.NewGuid(), "alice", "Appeal accepted."));
    }

    [Fact]
    public void AnEntry_MustNameItsTarget()
    {
        Assert.Throws<ArgumentException>(() =>
            new AuditLogEntry(Now, AdminAction.Reinstate, Guid.NewGuid(), "Scottie", Guid.Empty, "alice", "Appeal accepted."));
    }
}

public class GoldCorrectionTests
{
    [Fact]
    public void ACorrection_CanAddGold()
    {
        var player = new Player("alice", 200);

        player.CorrectGold(500);

        Assert.Equal(700, player.Gold);
    }

    [Fact]
    public void ACorrection_CanTakeGoldAway_DownToZero()
    {
        var player = new Player("alice", 200);

        player.CorrectGold(-200);

        Assert.Equal(0, player.Gold);
    }

    [Fact]
    public void ACorrection_CantLeaveGoldBelowZero()
    {
        var player = new Player("alice", 200);

        var error = Assert.Throws<InvalidOperationException>(() => player.CorrectGold(-201));

        Assert.Equal("alice has 200 gold, so at most 200 can be taken away.", error.Message);
        Assert.Equal(200, player.Gold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_001)]
    [InlineData(-100_001)]
    [InlineData(int.MinValue)]
    public void ACorrection_MustChangeSomethingWithinTheLimit(int change)
    {
        var player = new Player("alice", 200_000);

        Assert.ThrowsAny<ArgumentException>(() => player.CorrectGold(change));
        Assert.Equal(200_000, player.Gold);
    }

    [Fact]
    public void ACorrection_ChangesTheConcurrencyVersion()
    {
        var player = new Player("alice", 200);
        var before = player.Version;

        player.CorrectGold(1);

        Assert.NotEqual(before, player.Version);
    }
}
