using Game.Api.Observability;
using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Features;
using Game.Api.Hubs;
using Game.Api.Messaging;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.Services;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;

namespace Game.Api.Endpoints;

public static class BattleEndpoints
{
    public static void MapBattleEndpoints(this IEndpointRouteBuilder app)
    {
        // The server runs the battle: the client only picks a card each turn, and the outcome
        // and rewards are decided here.
        var group = app.MapGroup("/battles/pve")
                       .WithTags("Battles")
                       .RequireAuthorization();

        // Enter the arena. Returns the player's unfinished battle if there is one, so leaving
        // and coming back can't be used to re-roll a bad fight. ?difficulty=Heroic picks the
        // Heroic boss, which needs every card fully upgraded.
        group.MapPost("/", async (ClaimsPrincipal user, AppDbContext dbContext, TimeProvider timeProvider, IFeatureManager features,
            BossDifficulty difficulty = BossDifficulty.Normal) =>
        {
            var playerId = user.GetPlayerId();

            var existing = await dbContext.PveBattles
                .FirstOrDefaultAsync(b => b.PlayerId == playerId && b.Status == BattleStatus.InProgress);
            if (existing is not null)
            {
                return Results.Ok(BattleStateResponse.From(existing));
            }

            var player = await dbContext.Players.FindAsync(playerId);
            if (player is null)
            {
                return Results.NotFound("Player profile not found.");
            }

            if (!Enum.IsDefined(difficulty))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(difficulty)] = ["Unknown difficulty."]
                });
            }

            if (difficulty == BossDifficulty.Heroic && !await features.IsEnabledAsync(GameFeatures.HeroicBoss))
            {
                return GameFeatures.SwitchedOff(GameFeatures.HeroicBoss);
            }

            if (difficulty == BossDifficulty.Heroic && !player.CanFightHeroicBoss)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(difficulty)] = [$"Upgrade all three cards to level {BattleCards.MaxLevel} in the Gold Shop to face the Heroic boss."]
                });
            }

            // Upgrades bought in the Gold Shop come along, and a Battle Elixir is drunk now.
            var battle = PveBattle.Start(playerId, timeProvider.GetUtcNow().UtcDateTime, player.TakeLoadoutForBossFight(), difficulty);
            dbContext.PveBattles.Add(battle);
            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "Your gold changed at the same moment. Try again." });
            }

            return Results.Created($"/api/v1/battles/pve/{battle.Id}", BattleStateResponse.From(battle));
        });

        group.MapPost("/{battleId:guid}/turns", async (
            Guid battleId,
            PlayCardRequest request,
            ClaimsPrincipal user,
            AppDbContext dbContext,
            IBattleRandom random,
            TimeProvider timeProvider,
            MatchOutbox outbox,
            ArenaPulse pulse) =>
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

            if (!battle.CanPlay(request.Card))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PlayCardRequest.Card)] = [$"{BattleCards.Get(request.Card).Name} is still recharging."]
                });
            }

            var turn = battle.PlayCard(request.Card, random, timeProvider.GetUtcNow().UtcDateTime);

            BattleRewardResponse? reward = null;
            GameMatch? match = null;
            MatchCompletedEvent? completed = null;
            if (battle.IsFinished)
            {
                var player = await dbContext.Players.FindAsync(playerId);
                if (player is null)
                {
                    return Results.NotFound("Player profile not found.");
                }

                // Rewards and the match event are saved in the same SaveChanges as the final turn.
                var won = battle.Status == BattleStatus.Won;
                match = GameMatch.CreatePve(player.Id);
                dbContext.Matches.Add(match);
                var earned = new MatchRulesEngine(timeProvider).ProcessPveMatch(match, player, won, battle.Difficulty);
                battle.AttachMatch(match.Id);
                completed = new MatchCompletedEvent(match.Id, match.WinnerPlayerId!.Value, won ? GameMatch.AiBossId : player.Id)
                {
                    OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                    Kind = MatchKind.Boss,
                    Difficulty = battle.Difficulty,
                    Turns = battle.Turn,
                    Participants = [new MatchParticipant(player.Id, player.Username, battle.Class, won, earned.Gold, earned.Experience)]
                };
                outbox.Add(dbContext, completed);

                var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
                reward = BattleRewardResponse.From(earned, player, isDuel: false,
                    battle.Difficulty == BossDifficulty.Normal ? player.FullRewardBossWinsLeft(today) : null);
            }

            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "Another move was played at the same time. Reload the battle and try again." });
            }

            if (completed is not null)
            {
                outbox.Notify();
                pulse.Changed(); // the leaderboards may have moved
                GameTelemetry.BattleCompleted("pve", completed.WinnerId == playerId ? "victory" : "defeat", battle.Difficulty.ToString());
            }

            return Results.Ok(new PlayCardResponse(BattleStateResponse.From(battle), BattleTurnResponse.From(turn), reward));
        });
    }
}
