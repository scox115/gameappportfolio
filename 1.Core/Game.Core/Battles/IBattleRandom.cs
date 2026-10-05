namespace Game.Core.Battles;

// Source of randomness for boss attacks, so tests can make battles deterministic.
public interface IBattleRandom
{
    int Next(int minInclusive, int maxExclusive);
}
