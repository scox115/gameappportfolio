using Game.Core.Battles;
using Game.Core.Entities;

namespace Game.Api.Models;

/// <summary>A friend, or a hero with an open friend request, as the friends list shows them.</summary>
/// <param name="Online">The hero has the game open right now.</param>
/// <param name="DuelId">The duel they're fighting right now, which anyone can watch; null when they aren't in one.</param>
public record FriendView(Guid Id, string Username, string? Title, HeroClass Class, string? AvatarUrl, Cosmetic? Frame, int Rating,
    bool Online, Guid? DuelId);

/// <summary>The signed-in hero's friends, the requests waiting for their answer, and the ones they've sent.</summary>
public record FriendsResponse(IReadOnlyList<FriendView> Friends, IReadOnlyList<FriendView> Incoming, IReadOnlyList<FriendView> Outgoing,
    int MaxFriends);

/// <param name="Username">The name of the hero to befriend.</param>
public record FriendRequest(string Username);

/// <summary>A friend's invitation to a duel, pushed to every open browser of the hero they challenged.</summary>
public record ChallengeView(Guid Id, Guid FromId, string FromName, string? FromTitle, HeroClass FromClass, string? FromAvatarUrl,
    Cosmetic? FromFrame, int FromRating, int SecondsLeft);

/// <summary>Returned to the challenger: the challenge is open and waiting for the friend for this long.</summary>
public record ChallengeSent(Guid Id, string FriendName, int SecondsLeft);
