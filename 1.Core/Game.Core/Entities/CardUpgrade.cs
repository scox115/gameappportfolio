using Game.Core.Battles;

namespace Game.Core.Entities;

/// <summary>A card the player has upgraded past level 1 in the Gold Shop.</summary>
public class CardUpgrade
{
    public BattleCard Card { get; private set; }
    public int Level { get; private set; }

    private CardUpgrade() { }

    internal CardUpgrade(BattleCard card, int level)
    {
        Card = card;
        Level = level;
    }

    internal void LevelUp() => Level++;
}
