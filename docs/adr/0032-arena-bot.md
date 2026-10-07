# 0032. A bot to duel when nobody else is in the lobby

- **Status:** Accepted
- **Date:** 2026-10-07
- **Builds on:** [0002](0002-server-authoritative-battles.md) (the server plays every battle), [0027](0027-scale-out.md) (several replicas), [0030](0030-guest-play.md) (guest play)

## Context

Duels are the game's showpiece: two browsers, live over SignalR, with the server deciding every turn. But they need two players at once. Most visitors, a recruiter following a link among them, arrive alone, choose "Find an opponent" and wait for someone who never comes, so they never see a duel.

## Decision

- **A button in the lobby.** While a player waits, the lobby offers "Practice against the Arena Bot". It takes them out of the lobby and straight into a duel; after the duel, "Spar Again" starts another.
- **The bot is a hero the server plays.** `ArenaBot` (in `Game.Core/Battles`) has a fixed id and a hero row of its own, made the first time anyone duels it, with no sign-in account. Its name, "🤖 Arena Bot", starts with an emoji, which hero names can't contain, so no player can take it. It brings basic cards, as a new hero does, with a class picked at random each duel.
- **It plays like a cautious beginner.** It finishes off an opponent a Fireball can knock out, shields itself at 40% health or less, breaks a shield with the cheaper Fireball, and otherwise mixes Dragon Claw and Fireball. The rules are a few lines in the domain, tested without a server.
- **Its turns are played by the turn-timer worker.** Every replica already checks the duels every two seconds for turns that ran out (`PvpTurnTimeoutWorker`). It now also plays the bot's turn in any duel where the bot has had 1.2 seconds to "think", so a move comes 1–3 seconds after the player's. Turns go through the same `PlayCard` and broadcast as a person's. The battle's concurrency token stops two replicas playing the same turn, and nothing is lost if a replica stops: the next one to check plays it.
- **A practice duel.** It pays no gold or XP, moves no ratings, can't be wagered, and keeps the player's Duel Elixir for a duel that counts. That uses the existing practice rules ([`DuelRewardRules`](../../1.Core/Game.Core/Battles/DuelRewardRules.cs)), with its own explanation on the result screen. So beating the bot can't be farmed, and guests can duel it too.
- **It is left off the leaderboards and the hero count**, and the client doesn't offer to report it.

## Alternatives considered

- **Fill the lobby with the bot automatically after a wait.** Saves a click, but a player waiting for a friend would be pulled into a bot duel. The button lets the player choose, and it's there from the first second.
- **Play the bot's turn straight after the player's, in the same request.** Faster, but the reply would arrive before the player saw their own move. A delayed task in the API process would be lost if the replica stopped, and leaves the bot's timing to whichever replica the request reached. The worker that already runs every two seconds needs no new moving parts.
- **Let bot duels pay rewards.** It would make practice feel rewarding, but anyone could farm gold and rating from a bot that never gets better.
- **A stronger bot that looks ahead.** Each turn has three cards and a few random outcomes, so a search is possible, but a bot that wins every time is no fun for someone trying the game.

## Consequences

- Anyone can see a live duel without a second player, including a guest who has just arrived.
- Each worker check reads the in-progress duels where it's the bot's turn: one indexed query every two seconds per replica, alongside the timeout check it already makes.
- The bot's hero row appears in admin player searches, and its duels are kept and cleaned up like any other.
