using Game.Api.Messaging;
using Game.Core.Events;
using Game.Infrastructure.History;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace Game.Api.Workers;

// Builds match history and daily arena stats from the match events the API publishes.
public class MatchConsumerWorker : BackgroundService
{
    // How long to wait before handing a message back after a database error, so a database
    // outage doesn't spin through the queue.
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(2);

    // An event that still can't be saved after this many tries is dropped, so it can't block the queue.
    private const int MaxAttempts = 5;
    private readonly Dictionary<Guid, int> _failedAttempts = [];

    private readonly IConnectionFactory _connectionFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MatchConsumerWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private const string QueueName = Messaging.MatchTelemetryPublisher.MatchCompletedQueue;

    public MatchConsumerWorker(
        IConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<MatchConsumerWorker> logger)
    {
        _connectionFactory = connectionFactory;
        _scopeFactory = scopeFactory;
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
        _connection = await _connectionFactory.CreateConnectionAsync("card-arena-match-history-consumer", stoppingToken);
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
            MatchCompletedEvent? matchEvent;
            try
            {
                matchEvent = JsonSerializer.Deserialize<MatchCompletedEvent>(ea.Body.Span, MatchEventJson.Options);
            }
            catch (JsonException ex)
            {
                // A message we can't read will never become readable, so drop it instead of requeueing it forever
                _logger.LogError("Dropping unreadable match event: {Error} {Message}", ex.Message, Encoding.UTF8.GetString(ea.Body.Span));
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                return;
            }

            try
            {
                if (matchEvent is not null)
                {
                    await RecordAsync(matchEvent, stoppingToken);
                }

                // Acknowledge only after the history is saved, so a crash before this redelivers the event
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                if (matchEvent is not null) _failedAttempts.Remove(matchEvent.MatchId);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Usually the database is briefly unreachable, or another instance counted the same
                // day at the same moment. Hand the event back to try again; nothing was saved.
                var attempts = matchEvent is null ? MaxAttempts : _failedAttempts.GetValueOrDefault(matchEvent.MatchId) + 1;
                if (attempts >= MaxAttempts)
                {
                    _failedAttempts.Remove(matchEvent?.MatchId ?? Guid.Empty);
                    _logger.LogError(ex, "Giving up on match {MatchId} after {Attempts} tries.", matchEvent?.MatchId, attempts);
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                    return;
                }

                _failedAttempts[matchEvent!.MatchId] = attempts;
                _logger.LogWarning("Couldn't record match {MatchId} yet ({Error}); retrying (try {Attempts} of {Max}).",
                    matchEvent.MatchId, ex.Message, attempts, MaxAttempts);
                await Task.Delay(RequeueDelay, stoppingToken);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
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

    private async Task RecordAsync(MatchCompletedEvent matchEvent, CancellationToken cancellationToken)
    {
        // A fresh scope (and DbContext) per message, so a failed save can't leak into the next one.
        // This only writes history and stats: rewards were settled by the API when the match ended,
        // so this worker must never touch Player balances.
        using var scope = _scopeFactory.CreateScope();
        var projector = scope.ServiceProvider.GetRequiredService<MatchHistoryProjector>();
        var result = await projector.ProjectAsync(matchEvent, cancellationToken);

        _logger.LogInformation("📊 Match {MatchId} ({Kind}) winner {WinnerId}: {Result}",
            matchEvent.MatchId, matchEvent.Kind, matchEvent.WinnerId, result);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
