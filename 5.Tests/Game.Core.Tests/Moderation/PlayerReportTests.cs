using Game.Core.Moderation;

namespace Game.Core.Tests.Moderation;

public class PlayerReportTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AReport_WaitsUntilAnAdminResolvesIt_Once()
    {
        var report = new PlayerReport(Guid.NewGuid(), Guid.NewGuid(), ReportReason.Name, "  rude  ", Now);
        Assert.True(report.IsOpen);
        Assert.Equal("rude", report.Note);

        report.Resolve(ReportOutcome.Dismissed, Now.AddHours(1));

        Assert.False(report.IsOpen);
        Assert.Equal(ReportOutcome.Dismissed, report.Outcome);
        Assert.Throws<InvalidOperationException>(() => report.Resolve(ReportOutcome.ActionTaken, Now.AddHours(2)));
    }

    [Fact]
    public void ABlankNote_IsStoredAsNone()
    {
        Assert.Null(new PlayerReport(Guid.NewGuid(), Guid.NewGuid(), ReportReason.Portrait, "   ", Now).Note);
    }

    [Fact]
    public void BadReports_AreRefused()
    {
        var me = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new PlayerReport(me, me, ReportReason.Name, null, Now));
        Assert.Throws<ArgumentException>(() => new PlayerReport(me, Guid.NewGuid(), (ReportReason)7, null, Now));
        Assert.Throws<ArgumentException>(() => new PlayerReport(me, Guid.NewGuid(), ReportReason.Name, new string('x', PlayerReport.NoteMaxLength + 1), Now));
        Assert.Throws<ArgumentException>(() => new PlayerReport(me, Guid.NewGuid(), ReportReason.Name, null, DateTime.Now));
    }
}
