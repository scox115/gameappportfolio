# 0002. The server plays every battle; the client only picks cards

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

The first version let the browser report results (`POST /api/matches/pve/complete { IsVictory }`). Anyone with the browser's developer tools could award themselves wins, gold and XP, and a bug paid PvE rewards twice. A game with a leaderboard, an Elo rating and gold wagers can't trust the client.

## Decision

Battles are domain objects that live on the server and are saved after every turn:

- `POST /api/v1/battles/pve` starts (or resumes) a boss fight; `POST /api/v1/battles/pve/{id}/turns` plays one card. The client sends only the card it chose.
- The server rolls every die through `IBattleRandom`, applies the boss's move, and returns the new state. Rewards are paid once, when the battle ends, by the same request.
- Duels run the same way over SignalR (`ArenaHub`), with `PvpBattle` holding both players' state and `PvpTurnTimeoutWorker` ending turns after 30 seconds.
- Loadouts (card upgrades, class, elixirs) are copied into the battle when it starts, so buying an upgrade or switching class mid-fight changes nothing.

## Alternatives considered

- **Validate client-reported results.** There's nothing to validate against without replaying the fight on the server, which is this decision with extra steps.
- **Keep battle state only in memory.** Simpler, but a restart or scale-to-zero would lose every fight in progress. Saving each turn lets players resume a boss fight.

## Consequences

- Cheating needs a server bug, not a browser console.
- Every turn is a database write. The load test (`docs/load-testing.md`) shows this is fine: about 200 players fighting at once on one 0.5 CPU replica with a p95 of 203 ms.
- Tests replace `IBattleRandom` with fixed rolls, so whole fights are deterministic.
