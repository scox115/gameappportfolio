using System.Diagnostics;
using System.Text;
using Game.Api.Observability;
using Game.Infrastructure.Data;
using Game.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace Game.Api.Messaging;

/// <summary>
/// Sends the outbox to RabbitMQ, oldest first, over one long-lived connection. A row is deleted only
/// after the broker confirms it, so a crash or outage at any point means a resend, never a loss. The
/// consumer ignores a match it has already recorded, so a resend is harmless. While the broker is down
/// the rows simply wait in the database, however many there are and however long it takes.
/// </summary>
public class MatchOutboxRelay(
    MatchOutbox outbox,
    IServiceScopeFactory scopeFactory,
    IConnectionFactory connectionFactory,
    TelemetryBrokerStatus status,
    ILogger<MatchOutboxRelay> logger) : BackgroundService
{
    /// <summary>Rows sent per look at the table.</summary>
    public const int BatchSize = 50;

    /// <summary>
    /// How often the table is checked without a doorbell, to pick up rows saved just before a restart.
    /// New matches ring the doorbell, so this only matters after a crash.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Connect before looking for work, so the readiness check reflects the broker from startup.
                var channel = await GetChannelAsync(stoppingToken);
                var sent = await SendBatchAsync(channel, stoppingToken);
                failures = 0;
                if (sent < BatchSize)
                {
                    await outbox.WaitAsync(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = RabbitMqRetry.DelayFor(failures++);
                status.Disconnected(ex.Message);
                // The message is enough here; a stack trace on every retry would flood the log during an outage.
                logger.LogWarning("Could not send match events ({Error}); retrying in {Delay}s.", ex.Message, delay.TotalSeconds);
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

    // Sends up to one batch and returns how many went out.
    private async Task<int> SendBatchAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var batch = await dbContext.OutboxMessages
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;
        try
        {
            foreach (var message in batch)
            {
                await SendAsync(channel, message, cancellationToken);
                dbContext.OutboxMessages.Remove(message);
                sent++;
                GameTelemetry.TelemetryMessageSent();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            batch[sent].Failed(ex.Message);
            throw;
        }
        finally
        {
            // Record what went out (and the failure, if any) even when the batch stopped part way.
            // If this save fails, those rows are sent again later; the consumer skips repeats.
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
        }

        return sent;
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await CloseAsync();
        _connection = await connectionFactory.CreateConnectionAsync("card-arena-outbox-relay", cancellationToken);
        // Keep the readiness check current while idle: the connection can drop and recover between events.
        _connection.ConnectionShutdownAsync += (_, args) =>
        {
            status.Disconnected(args.ReplyText);
            return Task.CompletedTask;
        };
        _connection.RecoverySucceededAsync += (_, _) =>
        {
            status.Connected();
            return Task.CompletedTask;
        };
        // With publisher confirms, each publish waits until the broker has the message, so a row is
        // never deleted for a message the broker didn't take.
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await _channel.QueueDeclareAsync(
            queue: MatchOutbox.MatchCompletedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        status.Connected();
        logger.LogInformation("Connected to RabbitMQ for match events.");
        return _channel;
    }

    private static async Task SendAsync(IChannel channel, OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = GameTelemetry.ActivitySource.StartActivity("outbox publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", message.Queue);
        activity?.SetTag("messaging.message.id", message.Id.ToString());

        // Persistent, so messages in the durable queue also survive a broker restart.
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = message.Id.ToString(),
            Timestamp = new AmqpTimestamp(new DateTimeOffset(message.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: message.Queue,
            mandatory: false,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.Payload),
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
            logger.LogDebug(ex, "Ignoring an error while closing the outbox connection.");
        }
        finally
        {
            _channel = null;
            _connection = null;
        }
    }
}
