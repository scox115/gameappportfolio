using Game.Core.Entities;

namespace Game.Core.Services;

public class MatchRulesEngine
{
    public const int WinGold = 100;
    public const int WinExperience = 50;
    public const int LossGold = 20;
    public const int LossExperience = 10;

    /// <summary>
    /// Settles a PvP match between two registered players: finalizes the match and pays
    /// each player exactly once.
    /// </summary>
    public void ProcessMatchWin(GameMatch match, Player winner, Player loser)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(loser);

        if (winner.Id == loser.Id)
            throw new ArgumentException("Winner and loser must be different players.");
        if (!IsParticipant(match, winner.Id) || !IsParticipant(match, loser.Id))
            throw new ArgumentException("Both players must be participants in the match.");

        match.CompleteMatch(winner.Id);

        winner.AddGold(WinGold);
        winner.AddExperience(WinExperience);
        winner.RecordPvpWin();

        loser.AddGold(LossGold);
        loser.AddExperience(LossExperience);
    }

    /// <summary>
    /// Settles a PvE match against an AI boss: finalizes the match and pays the player
    /// either the win reward or the consolation reward, never both.
    /// </summary>
    public void ProcessPveMatch(GameMatch match, Player player, bool isVictory)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(player);

        if (!match.IsPve)
            throw new ArgumentException("Match is not a PvE match.");
        if (match.PlayerOneId != player.Id)
            throw new ArgumentException("Player is not the participant in this match.");

        match.CompleteMatch(isVictory ? player.Id : GameMatch.AiBossId);

        if (isVictory)
        {
            player.AddGold(WinGold);
            player.AddExperience(WinExperience);
        }
        else
        {
            player.AddGold(LossGold);
            player.AddExperience(LossExperience);
        }
    }

    private static bool IsParticipant(GameMatch match, Guid playerId) =>
        match.PlayerOneId == playerId || match.PlayerTwoId == playerId;
}
