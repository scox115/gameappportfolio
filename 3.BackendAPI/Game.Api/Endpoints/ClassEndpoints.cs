using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Endpoints;

public static class ClassEndpoints
{
    public static void MapClassEndpoints(this IEndpointRouteBuilder app)
    {
        // The playable classes, for the sign-up screen and the class switcher.
        app.MapGet("/classes", () => Results.Ok(HeroClasses.All.Select(HeroClassResponse.From).ToList()))
           .WithTags("Classes")
           .AllowAnonymous();

        // Switches the signed-in hero to another class for gold.
        app.MapPut("/players/me/class", async (ChangeClassRequest request, ClaimsPrincipal user, AppDbContext dbContext) =>
        {
            if (!Enum.IsDefined(request.Class))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(ChangeClassRequest.Class)] = ["Unknown class."]
                });
            }

            var player = await dbContext.Players.FindAsync(user.GetPlayerId());
            if (player is null)
            {
                return Results.NotFound("Player profile not found.");
            }

            try
            {
                player.ChangeClass(request.Class);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Can't change class");
            }

            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "Your gold changed at the same moment. Try again." });
            }

            return Results.Ok(PlayerProfileResponse.From(player));
        })
        .WithTags("Classes")
        .RequireAuthorization();
    }
}
