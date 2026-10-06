using Game.Core.Bounties;

namespace Game.Core.Tests;

public class FixedClock(DateOnly day) : TimeProvider
{
    /// <summary>The first day of 2026 whose bounties all need more than one battle.</summary>
    public static readonly DateOnly QuietDay = Enumerable.Range(0, 365)
        .Select(offset => new DateOnly(2026, 1, 1).AddDays(offset))
        .First(d => DailyBounties.For(d).All(b => b.Goal > 1));

    public override DateTimeOffset GetUtcNow() => new(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
}
