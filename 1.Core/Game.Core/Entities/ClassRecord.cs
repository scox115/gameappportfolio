using Game.Core.Battles;

namespace Game.Core.Entities;

/// <summary>A hero's PvP wins and losses while playing one class.</summary>
public class ClassRecord
{
    public HeroClass Class { get; private set; }
    public int Wins { get; private set; }
    public int Losses { get; private set; }

    private ClassRecord() { }

    internal ClassRecord(HeroClass heroClass)
    {
        Class = heroClass;
    }

    internal void AddWin() => Wins++;

    internal void AddLoss() => Losses++;
}
