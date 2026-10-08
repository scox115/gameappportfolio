# 0038. Match replays

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [0036](0036-spectating.md) (watching a duel), [0020](0020-account-export-and-deletion.md) (account deletion)

## Context

Once a duel ended, all that was left was a line in Recent Matches: who won and how many moves it took. Anyone who missed it live, or wanted to see where a close duel turned, had no way to watch it again. A duel only stored its current state (both heroes' health and shields), overwritten after every card.

## Decision

- **Record every card as it's played.** After each move the duel saves a `DuelMove` row: the turn, who played which card, what it did (damage, healing, blocked by a shield, or failed and why), and both heroes' health and shields afterwards. The row is written in the same save as the duel itself, so a replay can never disagree with how the duel actually went.
- **Replay the record, never re-roll.** Cards can miss or be blocked at random, so replaying from the starting state would need the same random numbers in the same order. Storing each result costs a small row per card and makes the replay exact, whatever changes to the card rules later.
- **Anyone can watch a replay,** as anyone can watch a duel live ([ADR 0036](0036-spectating.md)). `GET /api/v1/duels/{id}/replay` returns both heroes as they were at the start and every move. It answers 409 while the duel is still under way, so the page can offer the live view instead, and 404 when there's no such duel.
- **A player-paced page.** `/replay/{id}` starts playing straight away, one card every 1.2 seconds (or twice as fast), with buttons to pause, step back and forward a card, go back to the start, and skip to the result. Health bars, shields and the duel log follow the card shown.
- **Linked from where duels end.** The duel's result panel, the result on the watch page, and each duel in Recent Matches link to its replay. The match history looks up which of its duels have moves recorded, so duels from before replays existed show no link.
- **Kept as long as the duel.** Moves are deleted with their duel: by the cleanup worker after the 30 days finished battles are kept, and by account deletion ([ADR 0020](0020-account-export-and-deletion.md)) for every duel the hero fought.

## Alternatives considered

- **Store the random seed and replay the rules.** Smaller, but every future change to a card's damage or miss chance would silently change old replays, and the battle code would have to stay deterministic forever.
- **Store a JSON log on the duel row.** One fewer table, but the whole log would be rewritten on every move, and it couldn't be queried or cleaned up row by row.
- **Replays only for the two players.** Watching a duel live is already open to everyone, so hiding the same duel once it's over would protect nothing.

## Consequences

- Any duel fought from now on can be watched again, at the viewer's own pace, for 30 days.
- One more table (`DuelMoves`, added by the `DuelReplays` migration), with a row per card played and an index on the duel and turn.
- Duels fought before this change have no replay.
