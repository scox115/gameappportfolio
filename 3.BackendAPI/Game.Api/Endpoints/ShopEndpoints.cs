using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Core.Shop;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Endpoints;

public static class ShopEndpoints
{
    public static void MapShopEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shop")
                       .WithTags("Shop")
                       .RequireAuthorization();

        // What's for sale, priced for the signed-in player.
        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext dbContext) =>
        {
            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            return player is null
                ? Results.NotFound("Player profile not found.")
                : Results.Ok(ShopResponse.For(player));
        });

        // Buys one item. The server checks the price and the player's gold; the client only names the item.
        group.MapPost("/purchases", async (PurchaseRequest request, ClaimsPrincipal user, AppDbContext dbContext, ILogger<ShopResponse> logger) =>
        {
            if (!Enum.IsDefined(request.Item))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PurchaseRequest.Item)] = ["Unknown shop item."]
                });
            }

            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (player is null)
            {
                return Results.NotFound("Player profile not found.");
            }

            try
            {
                GoldShop.Buy(player, request.Item);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Can't buy that");
            }

            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another purchase or a battle reward changed this player's gold first.
                return Results.Conflict(new { message = "Your gold changed at the same moment. Try again." });
            }

            logger.LogInformation("Player {PlayerId} bought {Item}", player.Id, request.Item);
            return Results.Ok(ShopResponse.For(player));
        });

        // Chooses which owned title shows after the player's name (or none).
        group.MapPut("/title", async (EquipTitleRequest request, ClaimsPrincipal user, AppDbContext dbContext) =>
        {
            if (request.Title is { } title && !Enum.IsDefined(title))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(EquipTitleRequest.Title)] = ["Unknown title."]
                });
            }

            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (player is null)
            {
                return Results.NotFound("Player profile not found.");
            }

            try
            {
                player.EquipTitle(request.Title);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Can't use that title");
            }

            await dbContext.SaveChangesAsync();
            return Results.Ok(ShopResponse.For(player));
        });
    }
}
