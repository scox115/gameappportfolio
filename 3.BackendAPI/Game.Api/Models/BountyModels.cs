using Game.Core.Bounties;
using Game.Core.Entities;

namespace Game.Api.Models;

public record BountyResponse(Bounty Bounty, string Name, int Goal, int Progress, int Reward, bool Completed);

/// <param name="ResetsAt">When today's bounties are replaced (midnight UTC).</param>
/// <param name="StreakBonus">The extra gold the player's next win would pay.</param>
/// <param name="FullRewardBossWinsLeft">Boss wins left today that pay the full reward, out of <paramref name="FullRewardBossWinsPerDay"/>.</param>
/// <param name="HeroicUnlocked">Every card is fully upgraded, so the player can fight the Heroic boss.</param>
/// <param name="HeroicRewardAvailable">The player hasn't yet claimed today's Heroic reward.</param>
public record BountiesResponse(DateTime ResetsAt, int WinStreak, int StreakBonus, IReadOnlyList<BountyResponse> Bounties,
    int FullRewardBossWinsLeft, int FullRewardBossWinsPerDay, bool HeroicUnlocked, bool HeroicRewardAvailable)
{
    public static BountiesResponse For(Player player, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var nextBonus = Math.Min(player.WinStreak * Player.StreakBonusPerWin, Player.MaxStreakBonus);
        return new(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            player.WinStreak,
            nextBonus,
            player.BountiesFor(today)
                .Select(b => new BountyResponse(b.Definition.Bounty, b.Definition.Name, b.Definition.Goal,
                    Math.Min(b.Progress, b.Definition.Goal), b.Definition.Reward, b.Completed))
                .ToList(),
            player.FullRewardBossWinsLeft(today),
            Player.FullRewardBossWinsPerDay,
            player.CanFightHeroicBoss,
            player.HeroicRewardAvailable(today));
    }
}
