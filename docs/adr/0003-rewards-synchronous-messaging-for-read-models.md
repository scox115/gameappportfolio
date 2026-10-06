# 0003. Pay rewards in the request, use RabbitMQ only for read models

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

Originally the API published "match finished" to RabbitMQ and `MatchConsumerWorker` paid gold and XP in the background. That caused real bugs: the player's screen showed the old gold until the worker caught up, the worker raced the API when both changed the same player, and a lost or replayed message meant missing or double rewards.

## Decision

- **Rewards are written in the request that ends the battle**, in the same `SaveChanges` as the battle itself, guarded by optimistic concurrency ([0004](0004-optimistic-concurrency.md)). The player sees the reward in the response.
- **RabbitMQ carries facts for read models only.** After a battle ends, the API puts a `MatchCompletedEvent` (kind, difficulty, turns, participants, outcome) on a bounded in-memory channel. `MatchTelemetrySender` publishes it with retries, so a battle request never waits on the broker.
- `MatchConsumerWorker` builds match history and daily arena stats through `MatchHistoryProjector`. A unique index on (MatchId, PlayerId) makes it idempotent; messages are acknowledged only after the save, and database errors requeue up to five times.

## Alternatives considered

- **Transactional outbox.** The correct way to never lose an event. It wasn't worth it here: losing one history row is harmless now that money doesn't depend on the message. It's the first upgrade if an event ever matters more.
- **Azure Service Bus in the cloud.** It would mean a second broker and an abstraction over both. RabbitMQ runs as a sidecar container next to the API instead ([0007](0007-free-tier-azure-hosting.md)), the same broker as local development, at no extra cost.

## Consequences

- No reward can be lost, delayed or paid twice because of messaging.
- The history page and arena stats are eventually consistent, normally under a second behind.
- If the broker is down the game keeps working. `/health/ready` reports it as Degraded rather than Unhealthy, and up to 1,000 events wait in memory (oldest dropped first).
