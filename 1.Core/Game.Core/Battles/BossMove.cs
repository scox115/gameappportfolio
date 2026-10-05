namespace Game.Core.Battles;

public enum BossMove
{
    /// <summary>A normal hit.</summary>
    Slash,

    /// <summary>A heavy hit worth blocking.</summary>
    CrushingBlow,

    /// <summary>A lighter hit that heals the boss by the damage it deals.</summary>
    LifeDrain
}

public static class BossMoves
{
    public static string NameOf(BossMove move) => move switch
    {
        BossMove.Slash => "Slash",
        BossMove.CrushingBlow => "Crushing Blow",
        BossMove.LifeDrain => "Life Drain",
        _ => throw new ArgumentOutOfRangeException(nameof(move), move, "Unknown boss move.")
    };
}
