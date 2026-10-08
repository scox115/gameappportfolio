using System.Collections.Concurrent;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Hubs;

/// <summary>
/// Keeps track of who is online and tells every open lobby when the arena changes: a hero comes or
/// goes, joins or leaves the duel lobby, a duel starts or ends, or a match moves the leaderboards.
/// </summary>
/// <remarks>
/// The numbers are read from the database, so every replica agrees (see docs/adr/0034-live-lobby.md).
/// Changes only mark the pulse as changed; <see cref="Workers.ArenaPulseWorker"/> reads and broadcasts at
/// most once every couple of seconds, so a busy arena costs a few small queries, not one per move.
/// </remarks>
public class ArenaPulse(
    IServiceScopeFactory scopeFactory,
    IHubContext<LobbyHub, ILobbyClient> lobbyHub,
    TimeProvider timeProvider,
    ILogger<ArenaPulse> logger)
{
    // Signed-in connections to this replica, whose presence rows this replica keeps fresh.
    private readonly ConcurrentDictionary<string, Guid> _connectionsHere = new();
    private int _changed;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Something the lobby shows has changed; the next broadcast picks it up.</summary>
    public void Changed() => Interlocked.Exchange(ref _changed, 1);

    /// <summary>Counts the player as online. Never throws: being counted isn't worth refusing their session connection over.</summary>
    public async Task PlayerConnectedAsync(Guid playerId, string connectionId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OnlinePresence.Add(new OnlinePresence(connectionId, playerId, Now));
            await db.SaveChangesAsync();
            _connectionsHere[connectionId] = playerId;
            Changed();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not count player {PlayerId} as online.", playerId);
        }
    }

    /// <summary>Stops counting the connection. If this fails, the row goes stale and is cleared out anyway.</summary>
    public async Task PlayerDisconnectedAsync(string connectionId)
    {
        if (!_connectionsHere.TryRemove(connectionId, out _)) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.OnlinePresence.FindAsync(connectionId) is { } row)
        {
            db.OnlinePresence.Remove(row);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Already cleared out as stale.
            }
        }
        Changed();
    }

    /// <summary>
    /// Marks this replica's connections as still open, and clears out rows nobody has vouched for
    /// (left by a replica that stopped without saying goodbye).
    /// </summary>
    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var here = _connectionsHere.Keys.ToList();
        if (here.Count > 0)
        {
            var rows = await db.OnlinePresence.Where(p => here.Contains(p.ConnectionId)).ToListAsync(cancellationToken);
            foreach (var gone in here.Except(rows.Select(r => r.ConnectionId)))
            {
                _connectionsHere.TryRemove(gone, out _); // the account was deleted
            }
            foreach (var row in rows) row.StillHere(now);
        }

        var stale = now - OnlinePresence.StaleAfter;
        var abandoned = await db.OnlinePresence.Where(p => p.SeenAt < stale).ToListAsync(cancellationToken);
        db.OnlinePresence.RemoveRange(abandoned);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A browser closed between the read and the save; the next heartbeat catches up.
            logger.LogDebug(ex, "Presence heartbeat raced with a disconnect.");
        }
        if (abandoned.Count > 0) Changed();
    }

    /// <summary>Broadcasts the numbers to every open lobby, if anything changed since the last time.</summary>
    public async Task PublishIfChangedAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _changed, 0) == 0) return;
        try
        {
            await lobbyHub.Clients.All.PulseUpdated(await ReadAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Changed(); // try again on the next tick
            logger.LogWarning(ex, "Could not send the arena pulse.");
        }
    }

    public async Task<ArenaPulseView> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fresh = Now - OnlinePresence.StaleAfter;
        var freshInLobby = Now - PvpLobbyEntry.StaleAfter;

        var online = await db.OnlinePresence.Where(p => p.SeenAt >= fresh).Select(p => p.PlayerId).Distinct().CountAsync(cancellationToken);
        var waiting = await db.PvpLobby.CountAsync(e => e.SeenAt >= freshInLobby, cancellationToken);
        var duels = await db.PvpBattles.CountAsync(b => b.Status == PvpBattleStatus.InProgress, cancellationToken);
        var lastMatch = await db.Matches.OrderByDescending(m => m.CreatedAt).Select(m => (DateTime?)m.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return new ArenaPulseView(online, waiting, duels, lastMatch);
    }
}
