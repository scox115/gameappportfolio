namespace Game.Client.Models;

// The API's boss battle (PvE) shapes. The server owns the battle; the client replays what it decided.

public class BattleStateDto
{
    public Guid id { get; set; }
    public int playerHp { get; set; }
    public int playerMaxHp { get; set; } = 100;
    public int bossHp { get; set; }
    public int bossMaxHp { get; set; } = 160;
    public bool bossEnraged { get; set; }
    public string bossNextMove { get; set; } = "Slash";
    public string bossNextMoveName { get; set; } = "Slash";
    public int bossNextAttack { get; set; }
    public string? rechargingCard { get; set; }
    public int turn { get; set; }
    public string status { get; set; } = "";
    public List<BattleCardDto> cards { get; set; } = new();
    public string difficulty { get; set; } = "Normal";
    public string bossName { get; set; } = "The Shadow Overlord";
    public int enrageBelowPercent { get; set; } = 40;
}

public class BattleCardDto
{
    public string card { get; set; } = "";
    public int level { get; set; } = 1;
    public int damage { get; set; }
    public int heal { get; set; }
    public int failChance { get; set; }
}

public class BattleTurnDto
{
    public int turn { get; set; }
    public string cardName { get; set; } = "";
    public bool cardFailed { get; set; }
    public string? cardFailedReason { get; set; }
    public int damageDealt { get; set; }
    public int healthRestored { get; set; }
    public string? bossMoveName { get; set; }
    public int? bossDamage { get; set; }
    public bool attackBlocked { get; set; }
    public int bossHealed { get; set; }
}

public class BattleRewardDto
{
    public int goldEarned { get; set; }
    public int experienceEarned { get; set; }
    public PlayerProfileDto player { get; set; } = new();
    public int streakBonus { get; set; }
    public List<BountyRewardDto> bountiesCompleted { get; set; } = new();
    public bool reducedBossReward { get; set; }
    public int? fullRewardBossWinsLeft { get; set; }
}

public class BountyRewardDto
{
    public string name { get; set; } = "";
    public int reward { get; set; }
}

public class PlayCardResponseDto
{
    public BattleStateDto battle { get; set; } = new();
    public BattleTurnDto turnResult { get; set; } = new();
    public BattleRewardDto? reward { get; set; }
}

/// <summary>A boss battle the server just started (or resumed, when one was already in progress), for BossBattle to show.</summary>
public sealed record BossBattleStart(BattleStateDto Battle, bool Resumed);
