using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Caching;
using Game.Api.Hubs;

namespace Game.Api.Endpoints;

public static class PvpEndpoints
{
    public static void MapPvpEndpoints(this IEndpointRouteBuilder app)
    {
        // Moves happen over the SignalR hub; this lets a client check for a battle to resume.
        app.MapGet("/battles/pvp/current", async (ClaimsPrincipal user, PvpBattleService battles) =>
                await battles.GetActiveBattleViewAsync(user.GetPlayerId()) is { } battle
                    ? Results.Ok(battle)
                    : Results.NoContent())
            .WithTags("Battles")
            .RequireAuthorization();

        // GET: /api/v1/duels/live. Anyone can watch, signed in or not; the moves come over the lobby hub.
        app.MapGet("/duels/live", async (PvpBattleService battles) => Results.Ok(await battles.GetLiveDuelsAsync()))
            .WithTags("Battles")
            .AllowAnonymous()
            .CacheOutput(OutputCaching.Policies.LiveDuels);
    }
}
