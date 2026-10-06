using Game.Api.Features;
using Microsoft.Playwright;

namespace Game.E2E.Tests;

// Flips flags on the running API, as switching them in Azure App Configuration would.
public class FeatureFlagTests(ArenaFixture arena) : BrowserTest(arena)
{
    [Fact]
    public Task SwitchedOffFeatures_DisappearFromTown_AndComeBack() => WithScreenshotsOnFailureAsync(async () =>
    {
        try
        {
            Arena.Features.Set(GameFeatures.GoldShop, false);
            Arena.Features.Set(GameFeatures.Duels, false);

            var page = await CreateHeroAsync(NewHeroName("Flags"));

            await Assertions.Expect(page.GetByText("The Gold Shop is closed for now")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Duels are closed for now")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "GOLD SHOP" })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "LAUNCH BATTLE ARENA" })).ToBeVisibleAsync();
        }
        finally
        {
            Arena.Features.Set(GameFeatures.GoldShop, true);
            Arena.Features.Set(GameFeatures.Duels, true);
        }

        // A player who opens the game after the switch-back sees everything again.
        var later = await CreateHeroAsync(NewHeroName("Flags"));
        await Assertions.Expect(later.GetByRole(AriaRole.Button, new() { Name = "GOLD SHOP" })).ToBeVisibleAsync();
        await Assertions.Expect(later.GetByRole(AriaRole.Button, new() { Name = "FIND AN OPPONENT" })).ToBeVisibleAsync();
    });
}
