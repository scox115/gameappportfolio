using System.Collections.Concurrent;
using System.Text.Json;
using Game.Infrastructure.Data;
using Game.Infrastructure.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Api.ScaleOut;

/// <summary>
/// Passes SignalR messages between API replicas through the HubMessages table, so a player connected
/// to one replica sees what happens on another (their opponent's move, being signed out).
/// </summary>
/// <remarks>
/// <para>
/// Sending saves a row. Each replica reads rows from the last few seconds every
/// <see cref="ScaleOutOptions.PollInterval"/> and delivers those it hasn't seen and didn't send itself.
/// Reading by time rather than by id means a row whose id was handed out before a slower insert
/// committed is still picked up; remembering the ids delivered keeps each one to a single delivery.
/// </para>
/// <para>
/// A replica with no connections doesn't read at all, so the backplane never keeps an idle database awake.
/// See docs/adr/0027-scale-out.md for why this rather than Redis or Azure SignalR Service.
/// </para>
/// </remarks>
public sealed class SqlBackplane(
    IServiceScopeFactory scopeFactory,
    IOptions<ScaleOutOptions> options,
    IOptions<JsonHubProtocolOptions> json,
    TimeProvider timeProvider,
    ILogger<SqlBackplane> logger) : BackgroundService
{
    /// <summary>Delivers a message to this replica's connections: to a group when one is named, else to the users, else to everyone.</summary>
    public delegate Task Delivery(string method, IReadOnlyList<string>? userIds, string? group, object?[] args, CancellationToken cancellationToken);

    /// <summary>How far back each read looks, to cover inserts that commit late and small clock differences between replicas.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Delivery> _hubs = new();
    private readonly Dictionary<long, DateTime> _delivered = new();
    private int _connections;
    private DateTime _readFrom;

    /// <summary>This replica, so it can skip the messages it sent itself.</summary>
    public Guid ReplicaId { get; } = Guid.NewGuid();

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public void Subscribe(string hub, Delivery delivery) => _hubs[hub] = delivery;

    public void ConnectionOpened() => Interlocked.Increment(ref _connections);

    public void ConnectionClosed() => Interlocked.Decrement(ref _connections);

    /// <summary>Saves a message for the other replicas. The sender has already delivered it to its own connections.</summary>
    public async Task PublishAsync(string hub, string method, IReadOnlyList<string>? userIds, object?[] args, CancellationToken cancellationToken,
        string? group = null)
    {
        try
        {
            var arguments = JsonSerializer.Serialize(args, json.Value.PayloadSerializerOptions);
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.HubMessages.Add(new HubMessage(hub, method, userIds, arguments, ReplicaId, Now, group));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // What the message reports is already saved; a player on another replica catches up when their
            // page next reloads the battle. Failing the sender's move over it would be worse.
            logger.LogWarning(ex, "Could not pass {Method} on to the other replicas.", method);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _readFrom = Now;
        var nextCleanup = Now + CleanupInterval;
        var interval = options.Value.PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
                if (Volatile.Read(ref _connections) > 0)
                {
                    await ReadAsync(stoppingToken);
                }
                else
                {
                    // Nobody here to deliver to; start afresh when someone connects.
                    _readFrom = Now;
                }

                if (Now >= nextCleanup)
                {
                    nextCleanup = Now + CleanupInterval;
                    await DeleteOldAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A paused or unreachable database: try again next time round.
                logger.LogWarning(ex, "Could not read messages from the other replicas.");
            }
        }
    }

    /// <summary>Delivers the messages other replicas saved since the last read. Returns how many.</summary>
    public async Task<int> ReadAsync(CancellationToken cancellationToken = default)
    {
        var readStartedAt = Now;
        var since = _readFrom - Overlap;
        List<HubMessage> messages;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            messages = await scope.ServiceProvider.GetRequiredService<AppDbContext>().HubMessages.AsNoTracking()
                .Where(m => m.CreatedAt >= since && m.Origin != ReplicaId)
                .OrderBy(m => m.Id)
                .ToListAsync(cancellationToken);
        }

        var delivered = 0;
        foreach (var message in messages)
        {
            if (!_delivered.TryAdd(message.Id, message.CreatedAt)) continue;
            if (!_hubs.TryGetValue(message.Hub, out var deliver)) continue; // no connections to that hub here

            try
            {
                var args = JsonSerializer.Deserialize<JsonElement[]>(message.Arguments)!.Cast<object?>().ToArray();
                await deliver(message.Method, message.Recipients, message.Group, args, cancellationToken);
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not deliver {Method} from another replica.", message.Method);
            }
        }

        _readFrom = readStartedAt;
        // Forget ids too old to be read again.
        foreach (var (id, createdAt) in _delivered.ToList())
        {
            if (createdAt < since - Overlap) _delivered.Remove(id);
        }

        return delivered;
    }

    private async Task DeleteOldAsync(CancellationToken cancellationToken)
    {
        const int BatchSize = 500;
        var cutoff = Now - options.Value.MessageLifetime;
        // In batches, so a busy spell's backlog is cleared without one huge delete.
        for (var batch = 0; batch < 20; batch++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var old = await db.HubMessages.Where(m => m.CreatedAt < cutoff).OrderBy(m => m.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (old.Count == 0) return;

            db.HubMessages.RemoveRange(old);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return; // another replica is deleting the same rows; leave the rest to it
            }
            if (old.Count < BatchSize) return;
        }
    }
}
