using Game.Core.Battles;
using Game.Core.Bounties;
using Game.Core.Entities;

namespace Game.Core.Services;

/// <summary>What one player got from a finished battle.</summary>
/// <param name="Gold">All the gold paid out by this battle: the base reward, streak bonus, bounties and any wager winnings.</param>
/// <param name="Bonuses">The streak bonus and bounties included in <paramref name="Gold"/>.</param>
/// <param name="WagerResult">PvP only: the wager won (the payout) as a positive number, or the stake lost as a negative one.</param>
/// <param name="RatingChange">PvP only: rating points gained or lost.</param>
/// <param name="ReducedBossReward">A boss win past the daily full-reward limit, which pays <see cref="MatchRulesEngine.ReducedBossWinGold"/> and no streak bonus.</param>
public record BattleReward(int Gold, int Experience, BattleBonuses Bonuses, int WagerResult = 0, int RatingChange = 0, bool ReducedBossReward = false);

public record PvpSettlement(BattleReward Winner, BattleReward Loser);

public class MatchRulesEngine(TimeProvider timeProvider)
{
    public const int WinGold = 100;
    public const int WinExperience = 50;
    public const int LossGold = 20;
    public const int LossExperience = 10;

    /// <summary>What a boss win pays once the player has used today's full-reward wins.</summary>
    public const int ReducedBossWinGold = 25;

    // The Heroic boss pays much more, but only for the first win each day.
    public const int HeroicWinGold = 300;
    public const int HeroicWinExperience = 150;

    public MatchRulesEngine() : this(TimeProvider.System) { }

    // Daily bounties roll over at midnight UTC.
    private DateOnly Today => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Settles a PvP match between two registered players: finalizes the match and pays
    /// each player exactly once. <paramref name="wager"/> is what each player staked when the duel started.
    /// </summary>
    public PvpSettlement ProcessMatchWin(GameMatch match, Player winner, Player loser, int wager = 0,
        HeroClass? winnerClass = null, HeroClass? loserClass = null)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(loser);

        if (winner.Id == loser.Id)
            throw new ArgumentException("Winner and loser must be different players.");
        if (!IsParticipant(match, winner.Id) || !IsParticipant(match, loser.Id))
            throw new ArgumentException("Both players must be participants in the match.");
        if (!DuelWagers.IsAllowed(wager))
            throw new ArgumentOutOfRangeException(nameof(wager), wager, "That isn't one of the wager amounts.");

        match.CompleteMatch(winner.Id);

        var ratingChange = EloRating.PointsForWin(winner.Rating, loser.Rating);
        var payout = DuelWagers.Payout(wager);

        winner.AddGold(WinGold + payout);
        winner.AddExperience(WinExperience);
        winner.RecordPvpWin(ratingChange, winnerClass);
        var winnerBonuses = winner.RecordBattle(Today, BattleKind.Duel, won: true);

        // The rating floor can make the loser drop less than the winner gains.
        var loserRatingBefore = loser.Rating;
        loser.AddGold(LossGold);
        loser.AddExperience(LossExperience);
        loser.RecordPvpLoss(ratingChange, loserClass);
        var loserBonuses = loser.RecordBattle(Today, BattleKind.Duel, won: false);

        return new PvpSettlement(
            new BattleReward(WinGold + payout + winnerBonuses.Gold, WinExperience, winnerBonuses, payout, ratingChange),
            new BattleReward(LossGold + loserBonuses.Gold, LossExperience, loserBonuses, -wager, loser.Rating - loserRatingBefore));
    }

    /// <summary>
    /// Settles a PvE match against an AI boss: finalizes the match and pays the player
    /// either the win reward or the consolation reward, never both.
    /// </summary>
    public BattleReward ProcessPveMatch(GameMatch match, Player player, bool isVictory,
        BossDifficulty difficulty = BossDifficulty.Normal)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(player);

        if (!match.IsPve)
            throw new ArgumentException("Match is not a PvE match.");
        if (match.PlayerOneId != player.Id)
            throw new ArgumentException("Player is not the participant in this match.");

        match.CompleteMatch(isVictory ? player.Id : GameMatch.AiBossId);

        // Boss fights can be replayed endlessly, so only the first few wins each day pay in full.
        // The Heroic boss has its own limit of one, and doesn't use up the normal boss's wins.
        var heroic = difficulty == BossDifficulty.Heroic;
        var fullReward = !isVictory || (heroic ? player.RecordHeroicWin(Today) : player.RecordBossWin(Today));
        var gold = !isVictory ? LossGold
            : !fullReward ? ReducedBossWinGold
            : heroic ? HeroicWinGold : WinGold;
        var experience = !isVictory ? LossExperience
            : heroic && fullReward ? HeroicWinExperience : WinExperience;
        player.AddGold(gold);
        player.AddExperience(experience);
        var bonuses = player.RecordBattle(Today, BattleKind.BossFight, isVictory, payStreakBonus: fullReward);

        return new BattleReward(gold + bonuses.Gold, experience, bonuses, ReducedBossReward: !fullReward);
    }

    private static bool IsParticipant(GameMatch match, Guid playerId) =>
        match.PlayerOneId == playerId || match.PlayerTwoId == playerId;
}
