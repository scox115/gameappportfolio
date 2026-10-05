using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.Services; // Brings in MatchRulesEngine!
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace Game.Api.Endpoints;

public static class MatchEndpoints
{
    public const string MatchCompletedQueue = "match-completed-queue";

    public static void MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/matches").WithTags("Matches");

        // PvP: both participants are registered players.
        group.MapPost("/complete", async (
            CompleteMatchRequest request,
            AppDbContext dbContext,
            IConnectionFactory connectionFactory) =>
        {
            if (request.WinnerPlayerId == Guid.Empty || request.LoserPlayerId == Guid.Empty)
            {
                return Results.BadRequest("Player IDs cannot be empty.");
            }

            if (request.WinnerPlayerId == request.LoserPlayerId)
            {
                return Results.BadRequest("Winner and loser must be different players. Use /api/matches/pve/complete for boss fights.");
            }

            var winner = await dbContext.Players.FindAsync(request.WinnerPlayerId);
            if (winner is null) return Results.NotFound("Winner player profile entry missing.");

            var loser = await dbContext.Players.FindAsync(request.LoserPlayerId);
            if (loser is null) return Results.NotFound("Loser player profile entry missing.");

            var match = new GameMatch(winner.Id, loser.Id);
            dbContext.Matches.Add(match);

            // Rewards are applied and saved on the request thread. RabbitMQ only gets a telemetry notice.
            new MatchRulesEngine().ProcessMatchWin(match, winner, loser);
            await dbContext.SaveChangesAsync();

            await PublishTelemetryAsync(connectionFactory, new MatchCompletedEvent(match.Id, winner.Id, loser.Id));

            return Results.Ok(new { MatchId = match.Id, Status = "MatchProcessedAndGoldAwarded" });
        });

        // PvE: a registered player against an AI boss that has no Player row.
        group.MapPost("/pve/complete", async (
            CompletePveMatchRequest request,
            AppDbContext dbContext,
            IConnectionFactory connectionFactory) =>
        {
            if (request.PlayerId == Guid.Empty)
            {
                return Results.BadRequest("Player ID cannot be empty.");
            }

            var player = await dbContext.Players.FindAsync(request.PlayerId);
            if (player is null) return Results.NotFound("Player profile entry missing.");

            var match = GameMatch.CreatePve(player.Id);
            dbContext.Matches.Add(match);

            // Rewards are applied and saved on the request thread. RabbitMQ only gets a telemetry notice.
            new MatchRulesEngine().ProcessPveMatch(match, player, request.IsVictory);
            await dbContext.SaveChangesAsync();

            var winnerId = match.WinnerPlayerId!.Value;
            var loserId = winnerId == player.Id ? GameMatch.AiBossId : player.Id;
            await PublishTelemetryAsync(connectionFactory, new MatchCompletedEvent(match.Id, winnerId, loserId));

            return Results.Ok(new
            {
                MatchId = match.Id,
                IsVictory = request.IsVictory,
                Gold = player.Gold,
                Level = player.Level,
                ExperiencePoints = player.ExperiencePoints
            });
        });
    }

    private static async Task PublishTelemetryAsync(IConnectionFactory connectionFactory, MatchCompletedEvent matchEvent)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();
            using var channel = await connection.CreateChannelAsync();

            await channel.QueueDeclareAsync(
                queue: MatchCompletedQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(matchEvent));

            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: MatchCompletedQueue,
                body: body);
        }
        catch (Exception ex)
        {
            // Log broker error but don't crash the player's game if Docker queues back up
            System.Console.WriteLine($"[RabbitMQ Event Warning]: {ex.Message}");
        }
    }
}

public record CompleteMatchRequest(Guid WinnerPlayerId, Guid LoserPlayerId);

public record CompletePveMatchRequest(Guid PlayerId, bool IsVictory);
