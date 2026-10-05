namespace Game.Core.Entities;

/// <summary>Titles bought in the Gold Shop. They show next to a player's name and never change a battle.</summary>
public enum PlayerTitle
{
    Duelist,
    Gladiator,
    ArenaChampion
}

/// <param name="Name">How the title reads after the player's name, as in "Alice, the Gladiator".</param>
/// <param name="DuelWinsNeeded">PvP wins needed before the title can be bought.</param>
public record PlayerTitleDefinition(PlayerTitle Title, string Name, int Price, int DuelWinsNeeded);

public static class PlayerTitles
{
    private static readonly IReadOnlyDictionary<PlayerTitle, PlayerTitleDefinition> Definitions =
        new Dictionary<PlayerTitle, PlayerTitleDefinition>
        {
            [PlayerTitle.Duelist] = new(PlayerTitle.Duelist, "the Duelist", Price: 100, DuelWinsNeeded: 1),
            [PlayerTitle.Gladiator] = new(PlayerTitle.Gladiator, "the Gladiator", Price: 300, DuelWinsNeeded: 5),
            [PlayerTitle.ArenaChampion] = new(PlayerTitle.ArenaChampion, "Champion of the Arena", Price: 600, DuelWinsNeeded: 15)
        };

    public static IEnumerable<PlayerTitleDefinition> All => Definitions.Values;

    public static PlayerTitleDefinition Get(PlayerTitle title) =>
        Definitions.TryGetValue(title, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(title), title, "Unknown title.");
}

/// <summary>A title the player owns.</summary>
public class OwnedTitle
{
    public PlayerTitle Title { get; private set; }

    private OwnedTitle() { }

    internal OwnedTitle(PlayerTitle title) => Title = title;
}
