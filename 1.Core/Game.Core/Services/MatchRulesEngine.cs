using Game.Core.Entities;

namespace Game.Core.Services;

public class MatchRulesEngine
{
    public void ProcessMatchWin(GameMatch match, Player winner, Player loser)
    {
        // 1. Defensively guard against null profiles or data configurations
        if (match == null) throw new ArgumentNullException(nameof(match));

        // 2. Award rewards to the winner (Add 100 gold bounty and 50 experience points)
        if (winner != null)
        {
            // Assuming your Player entity methods match your domain design:
            winner.AddGold(100);
            winner.AddExperience(50);
        }

        // 3. Award minor consolation rewards to the losing player (20 gold, 10 experience)
        if (loser != null)
        {
            loser.AddGold(20);
            loser.AddExperience(10);
        }
    }
}
