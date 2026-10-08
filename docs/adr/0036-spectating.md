# 0036. Let anyone watch a duel under way

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [0034](0034-live-lobby.md) (the live lobby and its anonymous hub), [0027](0027-scale-out.md) (several replicas and the SQL backplane), [0032](0032-arena-bot.md) (the Arena Bot)

## Context

The live lobby shows how many duels are under way, but nobody outside a duel could see one. Watching other heroes play is how a new visitor learns the cards and how a player sizes up a rival. Every move is already pushed to the two players over SignalR the moment it's saved, so the moves only need a second audience.

## Decision

- **A list and a watch page, open to everyone.** `/watch` lists up to 20 duels under way, newest first, with both heroes, their health and the turn. It refreshes itself every five seconds. `/watch/{id}` shows one duel: both heroes side by side, whose turn it is with the turn timer, a log of every card, and the result when it ends. The "duels under way" number in the live lobby links to the list, so it's reachable from the sign-in page, the town and the leaderboards. Links to a duel can be shared.
- **`GET /api/v1/duels/live`**, anonymous, with a two-second output cache so a page of spectators costs one query every couple of seconds.
- **Moves come over the lobby hub, which anyone can connect to.** `LobbyHub.WatchDuel(id)` adds the connection to the SignalR group `duel-{id}` and returns the duel as it stands; `StopWatching(id)` leaves it. The connection joins before it reads, so a move made in between still arrives. A duel that's already over is returned with its result and the connection doesn't stay in the group.
- **Spectators see the duel from neither side.** `DuelWatchView` has both heroes' health, shields, classes, titles and ratings, whose turn it is and the result, and none of either player's cards or which one is recharging. After the two players are told about a move, the same move goes to the duel's group. A failure there is logged and doesn't fail the move: the spectator catches up with the next one.
- **The SQL backplane carries group messages.** It used to pass on only messages for users and for everyone. A `HubMessages.Group` column (the `SpectatorGroups` migration) lets a group message reach spectators connected to another replica. Each replica delivers it to the group members it holds. A duel's first broadcast isn't sent to its group, since nobody can be watching a duel that didn't exist a moment ago, which saves a row per duel.
- **The session check skips hubs open to visitors.** `ActiveSessionHubFilter` refused every call from a connection without a current session, which would refuse every visitor. It now lets calls through on hubs marked `[AllowAnonymous]`, which is only the lobby.
- **The browser ignores late moves.** The backplane re-reads the last few seconds of messages, so a spectator who joins mid-duel can be handed an update older than the duel they just loaded. The page drops any update whose turn is earlier than the one it shows, or that arrives after the result.

## Alternatives considered

- **Poll the duel over REST.** No hub changes, but each spectator would ask every second or so for nothing most of the time, and moves would show up late. The push already exists for the players.
- **Send each move to every lobby connection and let the browser filter.** No groups, so no backplane change, but every open copy of the game would receive every move of every duel.
- **Make spectators sign in.** Simpler session handling, but the point of the live lobby is that a visitor sees a living game before deciding to play. The duel shows nothing a player hasn't already shown their opponent.
- **Azure SignalR Service.** It handles groups across servers by itself, but it isn't free, and one nullable column does the job here.

## Consequences

- Anyone can follow a duel turn by turn, including the Arena Bot's practice duels, with no account.
- Duels are public. Usernames, portraits, titles and ratings were already on the leaderboards, so nothing new is exposed, but a hero can't hide a duel from spectators. If that's ever wanted, it becomes a setting checked by the list and `WatchDuel`.
- In SQL backplane mode each move writes a second `HubMessages` row, for its spectators, even when nobody is watching. Rows are small and cleared after a couple of minutes. If it matters, the replicas can track which groups have members and skip empty ones.
- Spectator counts per duel aren't shown. Group membership lives on each replica, so counting needs a shared table like the presence rows ([ADR 0034](0034-live-lobby.md)).
