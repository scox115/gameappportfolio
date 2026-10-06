using Game.Core.Entities;
using Game.Core.Events;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace Game.Api.Workers;

public class MatchConsumerWorker : BackgroundService
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<MatchConsumerWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private const string QueueName = Messaging.MatchTelemetryPublisher.MatchCompletedQueue;

    public MatchConsumerWorker(
        IConnectionFactory connectionFactory,
        ILogger<MatchConsumerWorker> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // RabbitMQ may not be up yet (or may be restarting), so keep trying instead of giving up.
        // Once connected, the client's automatic recovery reconnects the consumer after a drop.
        for (var attempt = 0; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await StartConsumingAsync(stoppingToken);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = Messaging.RabbitMqRetry.DelayFor(attempt);
                _logger.LogWarning("Telemetry consumer couldn't reach RabbitMQ ({Error}); retrying in {Delay}s.", ex.Message, delay.TotalSeconds);
                await CloseAsync();
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task StartConsumingAsync(CancellationToken stoppingToken)
    {
        _connection = await _connectionFactory.CreateConnectionAsync("card-arena-telemetry-consumer", stoppingToken);
        var channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        _channel = channel;

        // Ensure the target queue exists before reading from it
        await channel.QueueDeclareAsync(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (model, ea) =>
        {
            var messageJson = Encoding.UTF8.GetString(ea.Body.Span);

            try
            {
                var matchEvent = JsonSerializer.Deserialize<MatchCompletedEvent>(messageJson);
                if (matchEvent != null)
                {
                    RecordTelemetry(matchEvent);
                }

                // Acknowledge the message was processed so RabbitMQ can delete it
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read match telemetry message: {Message}", messageJson);
                // Drop malformed telemetry instead of requeueing it forever
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        _logger.LogInformation("Telemetry consumer listening on {Queue}.", QueueName);
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
            _logger.LogDebug(ex, "Ignoring an error while closing the telemetry consumer connection.");
        }
        finally
        {
            _channel = null;
            _connection = null;
        }
    }

    private void RecordTelemetry(MatchCompletedEvent matchEvent)
    {
        // Telemetry only. Rewards are applied and saved by the API on the request thread,
        // so this worker must never touch Player balances.
        var outcome = matchEvent.LoserId == GameMatch.AiBossId ? "PvE victory"
            : matchEvent.WinnerId == GameMatch.AiBossId ? "PvE defeat"
            : "PvP";

        _logger.LogInformation("📊 Match telemetry: {MatchId} ({Outcome}) winner {WinnerId}, loser {LoserId}",
            matchEvent.MatchId, outcome, matchEvent.WinnerId, matchEvent.LoserId);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
