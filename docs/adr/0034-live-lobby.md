# 0034. A live lobby: who is online, waiting and duelling

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [0027](0027-scale-out.md) (several replicas, state shared through SQL), [0032](0032-arena-bot.md) (the Arena Bot), [0016](0016-blue-green-deploys.md) (scale to zero)

## Context

The game is real time once a duel starts, but nothing showed that from outside one. A visitor landing on the sign-in page couldn't tell whether anyone else was playing. The town gave a player no reason to try a duel, and the leaderboards only changed when reloaded. The parts already existed: every signed-in browser keeps a SignalR connection open for single sign-in, and the duel lobby and duels live in shared SQL tables.

## Decision

- **Three live numbers:** heroes online (guests included, two tabs count once), heroes waiting for a duel, and duels under way (Arena Bot duels included). They show above the sign-in card, in the town and on the leaderboards, and change within a couple of seconds without a reload.
- **The leaderboards reload themselves.** The same feed carries when the latest match that can move them finished. An open leaderboard sees a newer one and quietly reloads, and screen readers hear "The rankings were just updated" once.
- **A hub anyone can listen to.** `LobbyHub` (`/hubs/lobby`) allows anonymous connections, so a visitor sees the numbers before signing up. It only sends: it has no methods to call, so it needs no session check. A new connection gets the current numbers at once.
- **Presence is shared through SQL, like the duel lobby.** Each signed-in browser's existing session connection adds a row to `OnlinePresence` when it opens and removes it when it closes. The replica holding the connection refreshes the row every 15 seconds, and rows nobody has refreshed for 45 seconds stop counting and are cleared. A replica that stops without warning is therefore out of the count within a minute. It's the same heartbeat as the duel lobby ([ADR 0027](0027-scale-out.md)).
- **Changes are coalesced, then broadcast.** Joining or leaving the lobby, a duel starting or ending, a boss fight finishing and a browser coming or going only mark the numbers as changed. `ArenaPulseWorker` reads them (four small indexed queries) and broadcasts at most once every two seconds, and only from the replica that saw the change. The SQL backplane carries the broadcast to the other replicas' connections.
- **The feed pauses when nobody is looking.** An open WebSocket keeps an API replica, and so the database, awake. The browser drops the feed once the page has been idle for the idle sign-out time, and reconnects on the next click or key, so a forgotten tab doesn't stop the game scaling to zero.

## Alternatives considered

- **Poll a REST endpoint.** Simpler and cacheable, but every open page would ask every few seconds whether anything changed, almost always for nothing. That keeps the API awake and adds requests that a push only sends when something happens.
- **Broadcast on every change, straight away.** Fine for one player, but a busy arena would send one SQL backplane message per move. Coalescing caps it at one every two seconds per replica that saw a change.
- **Count connections in each replica's memory.** No new table, but with several replicas each would only know its own players, which is the bug [ADR 0027](0027-scale-out.md) removed from the lobby.
- **Azure SignalR Service presence.** It tracks connections across servers by itself, but it isn't free, and the SQL tables already do the job at this scale.

## Consequences

- Visitors see a live game before they sign up, and players see when someone is waiting for a duel.
- One small table and one index on `Matches.CreatedAt`, added by the `LiveLobby` migration.
- Each replica runs four counting queries at most every two seconds while things are changing, and none while nothing is.
- A guest who left a tab open counts as online until the tab goes idle. Accounts that are deleted are removed from the count straight away.
