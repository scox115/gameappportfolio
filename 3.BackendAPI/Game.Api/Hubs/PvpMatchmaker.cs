using System.Collections.Concurrent;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Hubs;

/// <param name="SameNetwork">The two players connected from the same IP address.</param>
public record PvpPairing(Guid OpponentId, bool SameNetwork);

/// <summary>
/// Pairs players who are looking for a battle, first come first served. Players only meet
/// someone who chose the same wager.
/// </summary>
/// <remarks>
/// The queue is the PvpLobby table, so every API replica shares it (see docs/adr/0027-scale-out.md).
/// No locks are held: taking an opponent deletes their entry, and if another replica deleted it first
/// the save fails and the next opponent is tried. Each operation uses its own DbContext, so a retry
/// never disturbs the caller's changes.
/// </remarks>
public class PvpMatchmaker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<PvpMatchmaker> logger)
{
    // Players waiting through a connection to this replica, whose entries this replica keeps fresh.
    private readonly ConcurrentDictionary<Guid, byte> _waitingHere = new();

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Returns the longest-waiting opponent with the same wager, or null when the player is now waiting.
    /// Joining again with a different wager moves the player.
    /// </summary>
    /// <param name="network">The player's IP address, or null when it isn't known.</param>
    /// <param name="avoidSameNetwork">Skip opponents on the same network, so wagers can't move gold between one person's accounts.</param>
    public async Task<PvpPairing?> JoinOrPairAsync(Guid playerId, int wager = 0, string? network = null, bool avoidSameNetwork = false)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Join first, then look for an opponent. Of two players joining at once, at least one sees the
        // other, since each one's entry is saved before it looks. If both see each other and try to pair,
        // the deletes collide and only one save succeeds.
        var me = new PvpLobbyEntry(playerId, wager, network, Now);
        if (await db.PvpLobby.FindAsync(playerId) is { } old)
        {
            db.PvpLobby.Remove(old);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // paired a moment ago; joining again starts afresh
            }
        }
        db.PvpLobby.Add(me);
        await db.SaveChangesAsync();
        _waitingHere[playerId] = 0;

        while (true)
        {
            var freshSince = Now - PvpLobbyEntry.StaleAfter;
            var candidates = await db.PvpLobby
                .Where(e => e.Wager == wager && e.PlayerId != playerId && e.SeenAt >= freshSince)
                .OrderBy(e => e.JoinedAt)
                .Take(20)
                .ToListAsync();
            var opponent = candidates.FirstOrDefault(e => !(avoidSameNetwork && SameNetwork(e.Network, network)));
            if (opponent is null) return null;

            // Take the opponent and leave the lobby in one save. If either row is already gone, someone else
            // got there first: they either took this opponent (try the next) or took this player (wait for them).
            db.PvpLobby.RemoveRange(opponent, me);
            try
            {
                await db.SaveChangesAsync();
                _waitingHere.TryRemove(playerId, out _);
                return new PvpPairing(opponent.PlayerId, SameNetwork(opponent.Network, network));
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                if (await db.PvpLobby.FindAsync(playerId) is not { } stillWaiting)
                {
                    // Another player took this one; their replica is starting the duel and will tell us.
                    _waitingHere.TryRemove(playerId, out _);
                    return null;
                }
                me = stillWaiting;
            }
        }
    }

    public async Task LeaveAsync(Guid playerId)
    {
        _waitingHere.TryRemove(playerId, out _);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.PvpLobby.FindAsync(playerId) is not { } entry) return;

        db.PvpLobby.Remove(entry);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Already gone: paired or removed somewhere else.
        }
    }

    public async Task<bool> IsWaitingAsync(Guid playerId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PvpLobby.AnyAsync(e => e.PlayerId == playerId);
    }

    /// <summary>
    /// Marks the players waiting through this replica as still there, and clears out entries nobody has
    /// vouched for in a long while (left by a replica that stopped without saying goodbye).
    /// </summary>
    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var waitingHere = _waitingHere.Keys.ToList();
        if (waitingHere.Count > 0)
        {
            var entries = await db.PvpLobby.Where(e => waitingHere.Contains(e.PlayerId)).ToListAsync(cancellationToken);
            foreach (var gone in waitingHere.Except(entries.Select(e => e.PlayerId)))
            {
                _waitingHere.TryRemove(gone, out _); // paired, or left through another replica
            }
            foreach (var entry in entries) entry.StillHere(now);
        }

        var abandoned = now - PvpLobbyEntry.StaleAfter * 10;
        db.PvpLobby.RemoveRange(await db.PvpLobby.Where(e => e.SeenAt < abandoned).ToListAsync(cancellationToken));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A player was paired between the read and the save; the next heartbeat catches up.
            logger.LogDebug(ex, "Lobby heartbeat raced with a pairing.");
        }
    }

    // An unknown address never matches, so players are only treated as one network when we can tell.
    private static bool SameNetwork(string? a, string? b) => a is not null && b is not null && a == b;
}
