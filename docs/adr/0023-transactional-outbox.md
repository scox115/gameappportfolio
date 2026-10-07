# 0023. Send match events through a transactional outbox

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

After a battle ends, the API saves the rewards and then sends a `MatchCompletedEvent` to RabbitMQ, which builds match history and daily arena stats ([0003](0003-rewards-synchronous-messaging-for-read-models.md)). Until now the event went into a bounded in-memory queue after the save. That left two gaps:

- **Lost events.** If the API stopped after saving the match but before RabbitMQ took the event (a deploy, a crash, scale-to-zero during a broker outage), the event was gone. The player was paid, but the match never showed up in their history.
- **Dropped events.** During a long broker outage, more than 1,000 waiting events meant the oldest were dropped.

ADR 0003 named the transactional outbox as "the first upgrade if an event ever matters more". Match history, daily arena stats and players' data exports are all built from these events, so an event that silently goes missing is a bug players can see.

## Decision

- **The event is saved in the same `SaveChanges` as the match.** `MatchOutbox.Add` stages a row in a new `OutboxMessages` table (id = match id, queue, JSON payload, created time). The boss-fight endpoint and the duel service call it before their existing `SaveChanges`, so the rewards and the event are committed together, or neither is.
- **A relay sends it.** `MatchOutboxRelay` (a hosted service that replaces `MatchTelemetrySender`) reads the oldest 50 rows, publishes each one with RabbitMQ publisher confirms, and deletes a row only after the broker confirms it. A failed send records `Attempts` and `LastError` on the row and backs off up to 30 seconds.
- **It wakes up at once.** After a save, the API rings an in-process "doorbell" so the relay sends straight away. It also checks the table every 30 seconds, which picks up rows left by an earlier run that crashed.
- **Delivery is at least once.** A crash between the publish and the delete sends the row again. The consumer already skips a match it has recorded (unique index on match and hero), so a repeat is harmless.
- **Readiness shows the backlog.** `/health/ready` reports the message broker as Degraded with the number of events waiting in the outbox.

## Alternatives considered

- **Keep the in-memory queue.** It is simple and never slows a request, but it loses events on every restart during an outage, and Container Apps restarts the API often (deploys, scale-to-zero).
- **A library (MassTransit, NServiceBus, Wolverine).** They have good outboxes, but MassTransit and NServiceBus now need commercial licences, and any of them would replace the RabbitMQ code and consumer the project already has, for one event type.
- **Change data capture from SQL.** Not available on the Azure SQL free offer, and much heavier than one small table.
- **Write the event to RabbitMQ inside a distributed transaction.** RabbitMQ doesn't take part in SQL Server transactions, so this isn't possible without two-phase commit.

## Consequences

- No event is lost or dropped once its match is saved, however long the broker is down or however often the API restarts. A local check showed this: with RabbitMQ stopped, two matches were saved, the API was killed, and both events reached history once the broker and API came back.
- Each finished match writes one more small row, and the relay deletes it a moment later, so the table stays nearly empty. The 30-second check only runs while the API is running, so it doesn't stop the database from pausing when the game is idle.
- The relay assumes one API replica, as the rest of the API does today ([0006](0006-single-replica-signalr.md)). Two relays could both send the same row, which the consumer tolerates. When the API scales out, each relay should claim rows (for example with `UPDATE ... OUTPUT` and a lease) so they don't duplicate work.
- An event waiting in the outbox holds the hero names that appear in match history, for seconds normally and until the broker is back during an outage. If a hero deletes their account in that window, the consumer leaves them out, as before.
