using Game.Core.Battles;
using Game.Core.Events;

namespace Game.Core.History;

/// <summary>Totals for one UTC day across every hero, kept up to date by the match consumer.</summary>
public class DailyArenaStats
{
    public DateOnly Day { get; private set; }
    public int BossFights { get; private set; }
    public int BossWins { get; private set; }
    public int HeroicFights { get; private set; }
    public int HeroicWins { get; private set; }
    public int Duels { get; private set; }
    public int DuelKnockouts { get; private set; }
    public int DuelTimeouts { get; private set; }
    public int DuelForfeits { get; private set; }
    public int GoldPaid { get; private set; }

    /// <summary>Changes on every update, so two consumers can't both add to the same day at once.</summary>
    public Guid Version { get; private set; }

    private DailyArenaStats() { }

    public DailyArenaStats(DateOnly day)
    {
        Day = day;
        Version = Guid.NewGuid();
    }

    public void Record(MatchCompletedEvent match)
    {
        if (match.Kind == MatchKind.Boss)
        {
            var won = match.Participants.Any(p => p.Won);
            if (match.Difficulty == BossDifficulty.Heroic)
            {
                HeroicFights++;
                if (won) HeroicWins++;
            }
            else
            {
                BossFights++;
                if (won) BossWins++;
            }
        }
        else
        {
            Duels++;
            switch (match.EndReason)
            {
                case PvpEndReason.Knockout: DuelKnockouts++; break;
                case PvpEndReason.Timeout: DuelTimeouts++; break;
                case PvpEndReason.Forfeit: DuelForfeits++; break;
            }
        }

        GoldPaid += match.Participants.Sum(p => p.Gold);
        Version = Guid.NewGuid();
    }
}
