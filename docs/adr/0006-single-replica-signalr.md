# 0006. Real-time duels on SignalR with an in-memory lobby, one replica

- **Status:** Superseded by [0027](0027-scale-out.md)
- **Date:** 2026-10-05

## Context

Player-versus-player duels need two browsers to see each other's moves at once, a lobby that pairs players with the same wager, and turn timeouts.

## Decision

- **SignalR** (`ArenaHub` at `/hubs/arena`) pushes every turn to both players.
- **`PvpMatchmaker` is an in-memory queue** per wager, guarded by a lock. Battle state is still saved to SQL after every turn ([0002](0002-server-authoritative-battles.md)).
- **The API runs as exactly one replica** (`maxReplicas: 1` in `infra/main.bicep`).

## Alternatives considered

- **Azure SignalR Service with a shared queue** (Redis or a SQL table). This is how it would scale out, but the Free tier caps at 20 connections and the paid tier costs about $50 a month, against a free-tier budget ([0007](0007-free-tier-azure-hosting.md)).
- **Polling.** Simple, but slow turns and wasted requests.

## Consequences

- One replica is enough: the load test showed about 200 simultaneous players on a 0.5 CPU replica.
- A restart drops everyone waiting in the lobby; they press "Find an opponent" again. Duels already started survive because their state is in SQL.
- Scaling out needs three changes, each local to one class: an Azure SignalR backplane, a shared matchmaker queue, and lifting `maxReplicas`.
