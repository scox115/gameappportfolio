using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Social;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Social;

public enum FriendOutcome
{
    /// <summary>A request was sent.</summary>
    Requested,

    /// <summary>The two are now friends (the other hero had already asked, or this accepted their request).</summary>
    Friends,

    NotFound,
    Refused
}

public record FriendResult(FriendOutcome Outcome, string? Message = null);

/// <summary>
/// Friend requests and the friends list. Each change is pushed to the other hero's open browsers,
/// so their list updates without a reload (see docs/adr/0037-friends-and-challenges.md).
/// </summary>
public class FriendService(AppDbContext dbContext, TimeProvider timeProvider, SessionNotifier notifier)
{
    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<FriendsResponse> ListAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Friendships.AsNoTracking()
            .Where(f => f.RequesterId == playerId || f.AddresseeId == playerId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return new([], [], [], Friendship.MaxFriends);

        var ids = rows.Select(f => f.OtherThan(playerId)).ToList();
        var heroes = await dbContext.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var fresh = Now - OnlinePresence.StaleAfter;
        var online = (await dbContext.OnlinePresence.Where(p => ids.Contains(p.PlayerId) && p.SeenAt >= fresh)
            .Select(p => p.PlayerId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var duels = await dbContext.PvpBattles
            .Where(b => b.Status == PvpBattleStatus.InProgress && (ids.Contains(b.PlayerOneId) || ids.Contains(b.PlayerTwoId)))
            .Select(b => new { b.Id, b.PlayerOneId, b.PlayerTwoId })
            .ToListAsync(cancellationToken);
        var duelOf = new Dictionary<Guid, Guid>();
        foreach (var duel in duels)
        {
            duelOf[duel.PlayerOneId] = duel.Id;
            duelOf[duel.PlayerTwoId] = duel.Id;
        }

        FriendView? View(Guid id) => heroes.TryGetValue(id, out var p)
            ? new(p.Id, p.Username, p.TitleName, p.Class, p.AvatarUrl, p.EquippedFrame, p.Rating,
                online.Contains(p.Id), duelOf.TryGetValue(p.Id, out var duelId) ? duelId : null)
            : null;

        List<FriendView> Pick(Func<Friendship, bool> which) => rows.Where(which)
            .Select(f => View(f.OtherThan(playerId))).OfType<FriendView>()
            .OrderByDescending(f => f.Online).ThenBy(f => f.Username, StringComparer.OrdinalIgnoreCase).ToList();

        return new(
            Pick(f => f.IsAccepted),
            Pick(f => !f.IsAccepted && f.AddresseeId == playerId),
            Pick(f => !f.IsAccepted && f.RequesterId == playerId),
            Friendship.MaxFriends);
    }

    /// <summary>Asks a hero, by name, to be friends. If they had already asked this hero, it accepts instead.</summary>
    public async Task<FriendResult> RequestAsync(Guid playerId, string username, CancellationToken cancellationToken = default)
    {
        var me = await dbContext.Players.FindAsync([playerId], cancellationToken);
        if (me is null) return new(FriendOutcome.NotFound);
        if (me.IsGuest) return new(FriendOutcome.Refused, "Save your hero to add friends.");

        var name = username.Trim();
        // Names are matched the way sign-in matches them, ignoring case.
        var normalized = name.ToUpperInvariant();
        var otherId = await dbContext.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
        var other = otherId is { } id ? await dbContext.Players.FindAsync([id], cancellationToken) : null;
        if (other is null || other.Id == ArenaBot.Id) return new(FriendOutcome.NotFound, $"There's no hero called {name}.");
        if (other.Id == playerId) return new(FriendOutcome.Refused, "You can't add yourself.");
        if (other.IsGuest) return new(FriendOutcome.Refused, $"{other.Username} is playing as a guest, so they can't add friends yet.");

        var key = Friendship.KeyFor(playerId, other.Id);
        if (await dbContext.Friendships.FirstOrDefaultAsync(f => f.PairKey == key, cancellationToken) is { } existing)
        {
            if (existing.IsAccepted) return new(FriendOutcome.Refused, $"You and {other.Username} are already friends.");
            if (existing.RequesterId == playerId) return new(FriendOutcome.Refused, $"You've already asked {other.Username}.");

            // They asked first, so asking back says yes.
            existing.Accept(playerId, Now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await notifier.FriendsChangedAsync(other.Id);
            return new(FriendOutcome.Friends);
        }

        if (await CountAsync(playerId, cancellationToken) >= Friendship.MaxFriends)
        {
            return new(FriendOutcome.Refused, $"You can have up to {Friendship.MaxFriends} friends and requests. Remove one first.");
        }
        if (await CountAsync(other.Id, cancellationToken) >= Friendship.MaxFriends)
        {
            return new(FriendOutcome.Refused, $"{other.Username}'s friends list is full.");
        }

        dbContext.Friendships.Add(new Friendship(playerId, other.Id, Now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // They asked this hero at the same moment; the unique pair key kept one request.
            return new(FriendOutcome.Refused, $"{other.Username} just asked you too. Accept their request.");
        }
        await notifier.FriendsChangedAsync(other.Id);
        return new(FriendOutcome.Requested);
    }

    public async Task<FriendResult> AcceptAsync(Guid playerId, Guid otherId, CancellationToken cancellationToken = default)
    {
        var key = Friendship.KeyFor(playerId, otherId);
        var request = await dbContext.Friendships.FirstOrDefaultAsync(f => f.PairKey == key, cancellationToken);
        if (request is null || (!request.IsAccepted && request.AddresseeId != playerId)) return new(FriendOutcome.NotFound);

        request.Accept(playerId, Now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await notifier.FriendsChangedAsync(otherId);
        return new(FriendOutcome.Friends);
    }

    /// <summary>Removes a friend, turns down their request, or takes back one this hero sent. Removing nothing is fine.</summary>
    public async Task RemoveAsync(Guid playerId, Guid otherId, CancellationToken cancellationToken = default)
    {
        var key = Friendship.KeyFor(playerId, otherId);
        var row = await dbContext.Friendships.FirstOrDefaultAsync(f => f.PairKey == key, cancellationToken);
        if (row is null) return;

        dbContext.Friendships.Remove(row);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return; // the other hero removed it at the same moment
        }
        await notifier.FriendsChangedAsync(otherId);
    }

    /// <summary>Whether the two heroes are friends (an open request doesn't count).</summary>
    public Task<bool> AreFriendsAsync(Guid one, Guid two, CancellationToken cancellationToken = default)
    {
        var key = Friendship.KeyFor(one, two);
        return dbContext.Friendships.AnyAsync(f => f.PairKey == key && f.AcceptedAt != null, cancellationToken);
    }

    private Task<int> CountAsync(Guid playerId, CancellationToken cancellationToken) =>
        dbContext.Friendships.CountAsync(f => f.RequesterId == playerId || f.AddresseeId == playerId, cancellationToken);
}
