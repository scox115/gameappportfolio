using Game.Core.Battles;

namespace Game.Api.Models;

/// <param name="ChangePrice">Gold it costs to switch to this class after sign-up.</param>
public record HeroClassResponse(HeroClass Class, string Name, string Description, BattleCard SignatureCard, int BonusHp, int ChangePrice)
{
    public static HeroClassResponse From(HeroClassDefinition definition) =>
        new(definition.Class, definition.Name, definition.Description, definition.SignatureCard, definition.BonusHp, HeroClasses.ChangePrice);
}

public record ChangeClassRequest(HeroClass Class);
