using Game.Core.Entities;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            IConnectionFactory connectionFactory) => // Inject the free connection factory
        {
            if (request.WinnerPlayerId == Guid.Empty || request.LoserPlayerId == Guid.Empty)
            {
                return Results.BadRequest("Player IDs cannot be empty.");
            }

            // 1. Record the match shell permanently into our Docker SQL Server database
            var match = new GameMatch(request.WinnerPlayerId, request.LoserPlayerId);
            dbContext.Matches.Add(match);
            await dbContext.SaveChangesAsync();

            // 2. 🚀 NATIVE MESSAGING PIPELINE (Free & Open Source)
            // Establish a connection and open an operational channel to RabbitMQ
            using var connection = await connectionFactory.CreateConnectionAsync();
            using var channel = await connection.CreateChannelAsync();

            // Declare a safe queue endpoint named 'match-completed-queue'
            await channel.QueueDeclareAsync(
                queue: "match-completed-queue", 
                durable: true, 
                exclusive: false, 
                autoDelete: false, 
                arguments: null);

            // Serialize our data payload contract into a JSON string text format
            var eventPayload = new { MatchId = match.Id, WinnerId = request.WinnerPlayerId, LoserId = request.LoserPlayerId };
            var messageJson = JsonSerializer.Serialize(eventPayload);
            var body = Encoding.UTF8.GetBytes(messageJson);

            // Push the message raw bytes straight into the broker queue
            await channel.BasicPublishAsync(
                exchange: string.Empty, 
                routingKey: "match-completed-queue", 
                body: body);

            // Return 202 Accepted status indicating background processes have been successfully scheduled
            return Results.Accepted($"/api/matches/{match.Id}", new 
            { 
                MatchId = match.Id, 
                Status = "MatchRecorded_QueueNotificationPublished" 
            });
        });
    }
}

public record CompleteMatchRequest(Guid WinnerPlayerId, Guid LoserPlayerId);
