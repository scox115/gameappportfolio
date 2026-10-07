# 0027. More than one API replica, with shared state in SQL

- **Status:** Accepted
- **Date:** 2026-10-07
- **Supersedes:** [0006](0006-single-replica-signalr.md) (its one-replica limit; duels still run on SignalR)

## Context

[0006](0006-single-replica-signalr.md) capped the API at one replica because three things lived in one process: the duel lobby (an in-memory queue), SignalR connections (a message for a player only reached them if they were connected to the replica that sent it), and the outbox relay ([0023](0023-transactional-outbox.md)), which would send a row twice if two replicas ran it. One replica handles about 200 players fighting at once ([load test](../load-testing.md)), but it is also a single point of failure, and a popular moment would queue behind one 0.5 CPU container.

Container Apps already load-balances across replicas, so nothing is needed in front of the API. The work is making it safe for any request, or any live connection, to land on any replica.

## Decision

- **The lobby is a table.** `PvpLobby` holds one row per waiting player. Joining saves the player's row first and then looks for the longest-waiting player with the same wager; taking them deletes both rows in one save. No locks: if another replica took that player first, the delete finds the row gone, the save fails, and the next player is tried. Because each player's row is saved before they look, two players joining at once always find each other. The replica holding a waiting player's connection refreshes their row every 15 seconds, and rows nobody has refreshed for 45 seconds are ignored, so a replica that crashes can't leave ghosts that get paired.
- **Hub messages pass between replicas through SQL.** A custom `HubLifetimeManager` (SignalR's own in-process one, plus one step) delivers a message to the players connected locally and saves it to `HubMessages`. Every replica with connected players reads the last few seconds of rows four times a second and delivers those it didn't send and hasn't seen. Reading by time with a small overlap, rather than by id, means an insert that committed late is still picked up; remembering delivered ids keeps each message to one delivery. Rows are deleted after two minutes. A replica with no connections doesn't read, so this never keeps an idle database awake. `ScaleOut:Backplane` chooses: `None` (one replica), `Sql`, or `Redis` (SignalR's standard Redis backplane, with `ConnectionStrings:Redis`).
- **Browsers connect straight over WebSockets.** The client skips SignalR's separate negotiate request, which could otherwise reach a different replica from the WebSocket that follows it. This avoids sticky sessions, which Container Apps only offers in single-revision mode, and blue-green releases ([0016](0016-blue-green-deploys.md)) need multiple revisions.
- **The outbox relay claims rows.** Each relay marks a batch as claimed for a minute before sending it, with a concurrency token on the row, so when two replicas reach for the same rows one save fails and that relay looks again. Sent rows are deleted as before; rows a failed batch didn't reach are handed back at once. If a replica dies mid-batch, its claim runs out and another relay sends the rows.
- **Azure runs up to three replicas.** `maxReplicas` in `infra/main.bicep` (repository or environment variable `API_MAX_REPLICAS`, default 3) still scales to zero when idle and adds replicas at 50 concurrent requests each. Above one replica the deployment sets `ScaleOut:Backplane=Sql`; setting the variable to 1 returns to a single replica with no backplane.

## Alternatives considered

- **Azure SignalR Service.** The standard answer: replicas hand connections to the service, which fans messages out. The Free tier allows 20 concurrent connections, and every signed-in browser holds one or two, so about ten players; Standard costs about $50 a month ([0007](0007-free-tier-azure-hosting.md)).
- **Redis.** SignalR's Redis backplane is a few lines, and it is supported here (`ScaleOut:Backplane=Redis`). But Azure Cache for Redis has no free tier, and Redis as its own small container app costs a few dollars a month to keep running. The game's own database is already awake whenever anyone is playing, so passing messages through it adds no cost, at the price of up to a quarter of a second of delay between replicas.
- **Sticky sessions.** Would keep each browser on one replica, but two players in a duel can still be on different replicas, so a backplane is needed anyway, and Container Apps only offers it in single-revision mode.
- **Locking the lobby with `UPDLOCK, READPAST`.** The classic SQL Server queue pattern. Optimistic deletes do the same job without SQL Server-only hints, so the lobby and the relay claims are tested on SQLite and the in-memory provider too.

## Consequences

- Two replicas share the load, and a crashed replica costs its own connections, not the game. Duels already under way were always saved after each turn ([0002](0002-server-authoritative-battles.md)), so a player whose replica restarts reconnects and resumes.
- Messages between players on different replicas arrive up to about 250 ms later than between players on the same one. Every sent hub message is one extra row insert.
- Tests run the whole thing: two API hosts sharing one database pair players connected to different hosts and pass moves between them, and a SQLite file stands in for SQL Server when 40 players join through two replicas at once, or two relays drain the same outbox.
- Some things stay per replica, on purpose:
  - **Rate limits** count per replica, so with three replicas a determined client could get up to three times the limit. The limits are guards against brute force and spam, not quotas, and that stays true.
  - **The output cache** is per replica unless `ConnectionStrings:Redis` is set. A saved change evicts only the cache of the replica that saved it, so another replica can serve a leaderboard up to a minute old ([0010](0010-output-caching-with-tag-eviction.md)).
  - **RabbitMQ is a sidecar,** so each replica has its own broker; its relay and consumer use it, which works because the consumer ignores repeats. A replica removed while its broker still holds an unconsumed event loses that event's read-model update (match history), as a restart always could. Rewards are never affected ([0003](0003-rewards-synchronous-messaging-for-read-models.md)).
  - **Timers run on every replica.** The duel turn timer and the cleanup worker may do the same work twice; the battle's concurrency token ([0004](0004-optimistic-concurrency.md)) lets only one settle a duel, and cleanup deletes are idempotent.
