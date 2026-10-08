using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Api.Social;

namespace Game.Api.Endpoints;

/// <summary>
/// The friends list and friend requests. Challenging a friend to a duel happens over the arena hub
/// (see docs/adr/0037-friends-and-challenges.md).
/// </summary>
public static class FriendEndpoints
{
    public static void MapFriendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/friends").WithTags("Friends").RequireAuthorization();

        // GET: /api/v1/friends
        group.MapGet("/", async (ClaimsPrincipal user, FriendService friends, CancellationToken cancellationToken) =>
            Results.Ok(await friends.ListAsync(user.GetPlayerId(), cancellationToken)));

        // POST: /api/v1/friends { "username": "Aria" }. 201 when asked, 200 when that made them friends.
        group.MapPost("/", async (FriendRequest request, ClaimsPrincipal user, FriendService friends, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Enter a hero's name."] });
            }

            var result = await friends.RequestAsync(user.GetPlayerId(), request.Username, cancellationToken);
            return result.Outcome switch
            {
                FriendOutcome.Requested => Results.Created("/api/v1/friends", null),
                FriendOutcome.Friends => Results.Ok(),
                FriendOutcome.NotFound => Results.Problem(result.Message, statusCode: StatusCodes.Status404NotFound),
                _ => Results.Problem(result.Message, statusCode: StatusCodes.Status409Conflict),
            };
        })
        .RequireRateLimiting(RateLimits.FriendRequest);

        // POST: /api/v1/friends/{id}/accept
        group.MapPost("/{id:guid}/accept", async (Guid id, ClaimsPrincipal user, FriendService friends, CancellationToken cancellationToken) =>
            (await friends.AcceptAsync(user.GetPlayerId(), id, cancellationToken)).Outcome == FriendOutcome.Friends
                ? Results.Ok()
                : Results.NotFound());

        // DELETE: /api/v1/friends/{id}: unfriend, turn down their request, or take back yours.
        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, FriendService friends, CancellationToken cancellationToken) =>
        {
            await friends.RemoveAsync(user.GetPlayerId(), id, cancellationToken);
            return Results.NoContent();
        });

        // POST: /api/v1/friends/challenges/{id}/decline. The invite can be turned down from any screen,
        // without the arena connection that accepting needs. Turning down one that's gone is fine.
        group.MapPost("/challenges/{id:guid}/decline", async (Guid id, ClaimsPrincipal user, PvpBattleService battles) =>
        {
            await battles.DeclineChallengeAsync(user.GetPlayerId(), id);
            return Results.NoContent();
        });
    }
}
