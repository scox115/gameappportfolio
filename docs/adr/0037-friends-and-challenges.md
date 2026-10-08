# 0037. Friends, and challenging a friend to a duel

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [0034](0034-live-lobby.md) (presence through the session connection), [0036](0036-spectating.md) (watching a duel), [0027](0027-scale-out.md) (several replicas)

## Context

Duels paired whoever was waiting with the same wager, so two people who wanted to play each other had to join the lobby at the same moment and hope nobody else was waiting. There was no way to find someone again after a good duel, see whether they were online, or invite them.

## Decision

- **Friend requests by hero name.** A hero asks another by name, matched ignoring case as at sign-in. The other hero accepts or turns it down, and either can remove the friendship later. Asking someone who already asked you accepts their request. Guests can't add or be added, since a guest hero lasts only as long as its tab. Each hero can have up to 50 friends and open requests, and can send 30 requests an hour (a per-player rate limit, like reports).
- **One row per pair.** `Friendships` stores the requester, the addressee, when it was asked and when it was accepted. A `PairKey` column holds both ids in a fixed order with a unique index, so two heroes asking each other at the same moment can't create two rows. There are no foreign keys, as for reports and the lobby: account deletion removes a hero's friendships itself, and the data export lists them.
- **The list shows what matters for a duel.** `GET /api/v1/friends` returns friends, incoming requests and outgoing requests. Each friend shows class, rating, whether they're online (the presence rows from [ADR 0034](0034-live-lobby.md)), and the duel they're in, with a link to watch it ([ADR 0036](0036-spectating.md)). The town shows it as a Friends panel.
- **Changes arrive straight away.** Every signed-in browser already keeps a session connection open. It now also receives `FriendsChanged`, so a request or an answer shows up on the other hero's list without a reload. Online status is refreshed every 20 seconds.
- **Challenges go to the friend wherever they are.** "Challenge" on an online friend opens the duel screen and calls `ArenaHub.ChallengeFriend`. That saves a `DuelChallenge` row and pushes `ChallengeReceived` to every open browser of the friend over the session connection. They see a dialog on any screen, the watch page included, with 60 seconds to answer.
  - **Accept:** goes to the duel screen, which calls `AcceptChallenge`. The challenge row is deleted, so only one answer counts, and the duel starts as from the lobby: a coin flip decides who goes first and `MatchFound` goes to both players.
  - **Not now:** calls a small REST endpoint, which needs no arena connection, and the challenger hears `ChallengeDeclined`.
  - **Withdrawn:** if the challenger takes it back, leaves the duel screen or lets the minute run out, the friend's dialog closes through `ChallengeClosed`.
- **Challenges are friendly duels.** No wager, so friends can't pass gold between them. Between two accounts on the same network the duel is practice, as in the lobby. Rating, rewards and the daily repeat-opponent limit apply as for any duel.
- **One open challenge per hero.** `DuelChallenges` is keyed by the challenger, so sending a new challenge replaces the old one. The cleanup worker deletes any that a stopped replica left behind.

## Alternatives considered

- **Private lobbies with a code to share.** No friends list to build, but the code has to be passed outside the game. You also still can't see who's online.
- **Challenge over REST and poll for the answer.** No hub changes, but both browsers would poll for a minute. The session connection is already open on every screen.
- **Follow instead of mutual friendship.** One click, no requests to answer. But anyone could then see when a hero is online and invite them, which mutual consent avoids.
- **Wagers on challenges.** Fun, but a wager between two people who arranged to meet is the easiest way to move gold between accounts. The lobby's wager rules exist to stop exactly that.

## Consequences

- Players can find each other again, see who's online or in a duel, watch their friends, and start a duel with a friend without waiting in the queue.
- Two small tables, added by the `FriendsAndChallenges` migration, and two more client messages on the session connection.
- Online status can be up to 20 seconds old, plus the presence heartbeat ([ADR 0034](0034-live-lobby.md)).
- A challenge sent while the friend has no browser open is never seen. It runs out after a minute and the challenger is told they didn't answer. Offline invitations would need notifications outside the game.
