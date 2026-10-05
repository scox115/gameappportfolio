using System.Text;
using System.Text.Json;
using Game.Core.Events;
using RabbitMQ.Client;

namespace Game.Api.Messaging;

// Publishes a fire-and-forget telemetry notice after a match is settled. Rewards are
// already saved by then, so a broker outage must never fail the player's request.
public class MatchTelemetryPublisher(IConnectionFactory connectionFactory, ILogger<MatchTelemetryPublisher> logger)
{
    public const string MatchCompletedQueue = "match-completed-queue";

    public async Task PublishAsync(MatchCompletedEvent matchEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            await channel.QueueDeclareAsync(
                queue: MatchCompletedQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(matchEvent));

            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: MatchCompletedQueue,
                body: body,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish match telemetry for match {MatchId}.", matchEvent.MatchId);
        }
    }
}
