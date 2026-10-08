namespace Game.Client.Models;

// Shapes of the messages the server pushes (see Game.Api.Models.PvpModels).
public record PvpPlayerDto(Guid Id, string Username, string? Title, int Rating, string? AvatarUrl, string? Frame, int Hp, int MaxHp, bool Shielded, string Class = "Sorcerer");
public record PvpBattleDto(Guid Id, PvpPlayerDto You, PvpPlayerDto Opponent, bool YourTurn, int Turn,
    DateTime TurnDeadline, int TurnSecondsLeft, string? RechargingCard, string Status, bool? YouWon, int Wager, bool Practice, string? EndReason,
    List<PvpCardDto>? YourCards, bool AgainstBot = false);
public record PvpCardDto(string Card, int Level, int Damage, int Heal, int FailChance);
public record PvpTurnDto(int Turn, bool YourCard, string PlayerName, string CardName, bool CardFailed,
    string? CardFailedReason, int DamageDealt, int HealthRestored, bool AttackBlocked);
public record PvpProfileDto(Guid Id, string Username, int Gold, int Level, int Rating);
public record PvpRewardDto(int GoldEarned, int ExperienceEarned, PvpProfileDto Player, int? RatingChange,
    int StreakBonus, List<PvpBountyDto>? BountiesCompleted, int? WagerResult, string? NoRewardReason = null);
public record PvpBountyDto(string Name, int Reward);
public record ChallengeSentDto(Guid Id, string FriendName, int SecondsLeft);
public record PvpUpdateDto(PvpBattleDto Battle, PvpTurnDto? LastTurn, PvpRewardDto? Reward);
