using Game.Core.Entities;
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
    public static void MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/matches").WithTags("Matches");

        group.MapPost("/complete", async (
            CompleteMatchRequest request, 
            AppDbContext dbContext,
            IConnectionFactory connectionFactory) =>
        {
            if (request.WinnerPlayerId == Guid.Empty || request.LoserPlayerId == Guid.Empty)
            {
                return Results.BadRequest("Player IDs cannot be empty.");
            }

            // 1. FETCH BOTH PLAYERS FROM THE LIVE SQL DATABASE
            var winner = await dbContext.Players.FindAsync(request.WinnerPlayerId);
            var loser = await dbContext.Players.FindAsync(request.LoserPlayerId);

            // Create a placeholder if the opponent was a random AI boss generated on the frontend
            if (winner is null && request.WinnerPlayerId == winner?.Id) return Results.NotFound("Winner player profile entry missing.");
            
            // 2. CONSTRUCT THE MATCH SHIELD ENTITY
            var match = new GameMatch(request.WinnerPlayerId, request.LoserPlayerId);
            dbContext.Matches.Add(match);

            // 3. RUN THE GAME RECOGNITION LAW LOGIC IMMEDIATELY ON THE REQUEST THREAD
            // This guarantees the winning player gets their gold *before* the API returns!
            if (winner != null || loser != null)
            {
                var rulesEngine = new MatchRulesEngine();
                rulesEngine.ProcessMatchWin(match, winner, loser);
            }

            // 4. COMMIT EVERYTHING TO SQL SERVER Permanently
            await dbContext.SaveChangesAsync();

            // 5. EVENT GENERATION: Push notification payload to RabbitMQ for background tasks
            try
            {
                using var connection = await connectionFactory.CreateConnectionAsync();
                using var channel = await connection.CreateChannelAsync();

                await channel.QueueDeclareAsync(
                    queue: "match-completed-queue", 
                    durable: true, 
                    exclusive: false, 
                    autoDelete: false, 
                    arguments: null);

                var eventPayload = new { MatchId = match.Id, WinnerId = request.WinnerPlayerId, LoserId = request.LoserPlayerId };
                var messageJson = JsonSerializer.Serialize(eventPayload);
                var body = Encoding.UTF8.GetBytes(messageJson);

                await channel.BasicPublishAsync(
                    exchange: string.Empty, 
                    routingKey: "match-completed-queue", 
                    body: body);
            }
            catch (Exception ex)
            {
                // Log broker error but don't crash the player's game if Docker queues back up
                System.Console.WriteLine($"[RabbitMQ Event Warning]: {ex.Message}");
            }

            // Return a 200 OK indicating transactions are completely finished and locked in
            return Results.Ok(new { MatchId = match.Id, Status = "MatchProcessedAndGoldAwarded" });
        });
    }
}

// --- 🛡️ RESTORE THIS DATA CONTRACT RECORD AT THE BOTTOM OF THE FILE ---
public record CompleteMatchRequest(Guid WinnerPlayerId, Guid LoserPlayerId);
