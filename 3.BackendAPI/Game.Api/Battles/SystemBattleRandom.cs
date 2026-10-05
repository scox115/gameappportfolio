using Game.Core.Battles;

namespace Game.Api.Battles;

public class SystemBattleRandom : IBattleRandom
{
    public int Next(int minInclusive, int maxExclusive) => Random.Shared.Next(minInclusive, maxExclusive);
}
