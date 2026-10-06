using System.Text.Json;
using Game.Core.Events;
using RabbitMQ.Client;

namespace Game.Api.Messaging;

// Sends queued match telemetry to RabbitMQ over one long-lived connection and channel. If the broker
// is unreachable it keeps the unsent event, waits (backing off up to 30 seconds) and reconnects.
public class MatchTelemetrySender(
    MatchTelemetryPublisher publisher,
    IConnectionFactory connectionFactory,
    ILogger<MatchTelemetrySender> logger) : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MatchCompletedEvent? unsent = null;
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                unsent ??= await publisher.Pending.ReadAsync(stoppingToken);
                var channel = await GetChannelAsync(stoppingToken);
                await SendAsync(channel, unsent, stoppingToken);
                unsent = null;
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = RabbitMqRetry.DelayFor(failures++);
                // The message is enough here; a stack trace on every retry would flood the log during an outage.
                logger.LogWarning("Could not send match telemetry ({Error}); retrying in {Delay}s.", ex.Message, delay.TotalSeconds);
                await CloseAsync();
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await CloseAsync();
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await CloseAsync();
        _connection = await connectionFactory.CreateConnectionAsync("card-arena-telemetry-sender", cancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(
            queue: MatchTelemetryPublisher.MatchCompletedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        logger.LogInformation("Connected to RabbitMQ for match telemetry.");
        return _channel;
    }

    private static async Task SendAsync(IChannel channel, MatchCompletedEvent matchEvent, CancellationToken cancellationToken)
    {
        // Persistent, so messages in the durable queue also survive a broker restart.
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = matchEvent.MatchId.ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: MatchTelemetryPublisher.MatchCompletedQueue,
            mandatory: false,
            basicProperties: properties,
            body: JsonSerializer.SerializeToUtf8Bytes(matchEvent),
            cancellationToken: cancellationToken);
    }

    private async Task CloseAsync()
    {
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring an error while closing the telemetry connection.");
        }
        finally
        {
            _channel = null;
            _connection = null;
        }
    }
}
