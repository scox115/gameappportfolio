using Game.Core.Entities;
using Game.Core.Services;
using Game.Infrastructure.Data;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace Game.Api.Workers;

public class MatchConsumerWorker : BackgroundService
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly IServiceProvider _serviceProvider; // Required to safely resolve Scoped DbContext inside a Singleton background worker
    private readonly ILogger<MatchConsumerWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private const string QueueName = "match-completed-queue";

    public MatchConsumerWorker(
        IConnectionFactory connectionFactory, 
        IServiceProvider serviceProvider, 
        ILogger<MatchConsumerWorker> logger)
    {
        _connectionFactory = connectionFactory;
        _serviceProvider = serviceProvider;
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
                    var payload = JsonSerializer.Deserialize<MatchMessagePayload>(messageJson);
                    if (payload != null)
                    {
                        await ProcessMatchRewardsAsync(payload);
                    }

                    // Acknowledge the message was successfully processed so RabbitMQ can safely delete it
                    await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to process match message payload rewards.");
                    // Negative Acknowledge: put the message back on the queue to retry later
                    await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
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

    private async Task ProcessMatchRewardsAsync(MatchMessagePayload payload)
    {
        // Open a clean dependency injection scope to fetch the database cleanly on a background thread
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1. Fetch the exact Player profiles from the Docker SQL Database
        var winner = await dbContext.Players.FindAsync(payload.WinnerId);
        var loser = await dbContext.Players.FindAsync(payload.LoserId);
        var match = await dbContext.Matches.FindAsync(payload.MatchId);

        if (winner == null || loser == null || match == null)
        {
            _logger.LogWarning("⚠️ Could not process rewards. Database entities missing for Match {MatchId}", payload.MatchId);
            return;
        }

        // 2. Invoke our untainted Game Core domain rules engine to referee the scoring parameters
        var rulesEngine = new MatchRulesEngine();
        rulesEngine.ProcessMatchWin(match, winner, loser);

        // 3. Save the modified gold balances and experience points permanently back to SQL Server
        await dbContext.SaveChangesAsync();

        _logger.LogInformation("🏆 Rewards successfully processed! {Winner} earned gold/XP. {Loser} received consolation prizes.", winner.Username, loser.Username);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}

// Lightweight JSON parsing model container blueprint
public record MatchMessagePayload(Guid MatchId, Guid WinnerId, Guid LoserId);
