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
        try
        {
            // 1. Establish the connection channels down to port 5672 inside Docker
            _connection = await _connectionFactory.CreateConnectionAsync(stoppingToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

            // 2. Ensure the target queue exists before reading from it
            await _channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);

            _logger.LogInformation("🚀 Free RabbitMQ Background Consumer Worker initialized and listening on {Queue}...", QueueName);

            // 3. Set up the event-driven consumer listener pipeline
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var messageJson = Encoding.UTF8.GetString(body);
                
                _logger.LogInformation("📨 Raw message pulled off the queue: {Message}", messageJson);

                try
                {
                    // 4. Parse the message body text contract back into a usable payload object
                    var matchEvent = JsonSerializer.Deserialize<MatchCompletedEvent>(messageJson);
                    if (matchEvent != null)
                    {
                        RecordTelemetry(matchEvent);
                    }

                    // Acknowledge the message was successfully processed so RabbitMQ can safely delete it
                    await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to read match telemetry message.");
                    // Drop malformed telemetry instead of requeueing it forever
                    await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                }
            };

            // Start consuming text packets off the queue stream
            await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);

            // Keep the background loop alive until .NET tells the host process to shut down
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "💥 Critical crash occurred inside the background messaging consumer runtime.");
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
