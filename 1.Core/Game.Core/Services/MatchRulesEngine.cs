using Game.Core.Entities;

namespace Game.Core.Services;

public class MatchRulesEngine
{
    private const int BaseGoldReward = 50;
    private const int BaseXpReward = 120;

    /// <summary>
    /// Processes rewards safely for a finished match. 
    /// Enforces domain boundary rules entirely decoupled from databases or HTTP context elements.
    /// </summary>
    public void ProcessMatchWin(GameMatch match, Player winner, Player loser)
    {
        if (winner.Id != match.PlayerOneId && winner.Id != match.PlayerTwoId)
            throw new InvalidOperationException("Provided winning player did not participate in this match.");

        // 1. Mutate Match status state safely
        match.CompleteMatch(winner.Id);

        // 2. Grant rewards to the winner based on business rules parameters
        winner.AddGold(BaseGoldReward);
        winner.AddExperience(BaseXpReward);

        // 3. Consolation rewards for the loser
        loser.AddGold(BaseGoldReward / 2);
        loser.AddExperience(BaseXpReward / 3);
    }
}
