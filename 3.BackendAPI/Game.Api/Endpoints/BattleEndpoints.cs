using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Messaging;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.Services;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Endpoints;

public static class BattleEndpoints
{
    public static void MapBattleEndpoints(this IEndpointRouteBuilder app)
    {
        // The server runs the battle: the client only picks a card each turn, and the outcome
        // and rewards are decided here.
        var group = app.MapGroup("/api/battles/pve")
                       .WithTags("Battles")
                       .RequireAuthorization();

        // Enter the arena. Returns the player's unfinished battle if there is one, so leaving
        // and coming back can't be used to re-roll a bad fight.
        group.MapPost("/", async (ClaimsPrincipal user, AppDbContext dbContext, TimeProvider timeProvider) =>
        {
            var playerId = user.GetPlayerId();

            var existing = await dbContext.PveBattles
                .FirstOrDefaultAsync(b => b.PlayerId == playerId && b.Status == BattleStatus.InProgress);
            if (existing is not null)
            {
                return Results.Ok(BattleStateResponse.From(existing));
            }

            if (!await dbContext.Players.AnyAsync(p => p.Id == playerId))
            {
                return Results.NotFound("Player profile not found.");
            }

            var battle = PveBattle.Start(playerId, timeProvider.GetUtcNow().UtcDateTime);
            dbContext.PveBattles.Add(battle);
            await dbContext.SaveChangesAsync();

            return Results.Created($"/api/battles/pve/{battle.Id}", BattleStateResponse.From(battle));
        });

        group.MapPost("/{battleId:guid}/turns", async (
            Guid battleId,
            PlayCardRequest request,
            ClaimsPrincipal user,
            AppDbContext dbContext,
            IBattleRandom random,
            TimeProvider timeProvider,
            MatchTelemetryPublisher telemetry) =>
        {
            var playerId = user.GetPlayerId();

            // Another player's battle looks the same as a missing one.
            var battle = await dbContext.PveBattles.FirstOrDefaultAsync(b => b.Id == battleId && b.PlayerId == playerId);
            if (battle is null)
            {
                return Results.NotFound("Battle not found.");
            }

            if (battle.IsFinished)
            {
                return Results.Conflict(new { message = "This battle is already over." });
            }

            if (!Enum.IsDefined(request.Card))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PlayCardRequest.Card)] = ["Unknown card."]
                });
            }

            var turn = battle.PlayCard(request.Card, random, timeProvider.GetUtcNow().UtcDateTime);

            BattleRewardResponse? reward = null;
            GameMatch? match = null;
            if (battle.IsFinished)
            {
                var player = await dbContext.Players.FindAsync(playerId);
                if (player is null)
                {
                    return Results.NotFound("Player profile not found.");
                }

                // Rewards are settled in the same SaveChanges as the final turn.
                var won = battle.Status == BattleStatus.Won;
                var goldBefore = player.Gold;
                match = GameMatch.CreatePve(player.Id);
                dbContext.Matches.Add(match);
                new MatchRulesEngine().ProcessPveMatch(match, player, won);
                battle.AttachMatch(match.Id);

                reward = new BattleRewardResponse(
                    player.Gold - goldBefore,
                    won ? MatchRulesEngine.WinExperience : MatchRulesEngine.LossExperience,
                    PlayerProfileResponse.From(player));
            }

            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "Another move was played at the same time. Reload the battle and try again." });
            }

            if (match is not null)
            {
                var winnerId = match.WinnerPlayerId!.Value;
                var loserId = winnerId == playerId ? GameMatch.AiBossId : playerId;
                await telemetry.PublishAsync(new MatchCompletedEvent(match.Id, winnerId, loserId));
            }

            var turnResponse = new BattleTurnResponse(turn.Turn, turn.Card.Name, turn.DamageDealt, turn.HealthRestored, turn.BossDamage);
            return Results.Ok(new PlayCardResponse(BattleStateResponse.From(battle), turnResponse, reward));
        });
    }
}
