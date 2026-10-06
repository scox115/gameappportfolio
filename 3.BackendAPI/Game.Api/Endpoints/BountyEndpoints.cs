using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Infrastructure.Data;

namespace Game.Api.Endpoints;

public static class BountyEndpoints
{
    public static void MapBountyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bounties")
                       .WithTags("Bounties")
                       .RequireAuthorization();

        // Today's bounties with the signed-in player's progress, and their current win streak.
        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext dbContext, TimeProvider timeProvider) =>
        {
            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (player is null)
            {
                return Results.NotFound("Player profile not found.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            return Results.Ok(BountiesResponse.For(player, now));
        });
    }
}
