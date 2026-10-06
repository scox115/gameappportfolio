# 0014. Tune game balance with simulations, not guesses

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

Boss difficulty, card upgrades, classes and reward amounts interact in ways that are hard to judge by playing a few rounds. Early changes based on feel made the boss either trivial or impossible.

## Decision

Before a balance change ships, a throwaway console app references `Game.Core` and plays 20,000 fights per scenario with simple strategies (greedy and smart), reporting win rates. Because the rules live in a framework-free domain ([0001](0001-clean-architecture.md)) with injectable randomness, the simulation runs the exact production code.

Targets the current numbers were tuned to:

- Normal boss: about 22% for a new player playing well, about 82% for a fully upgraded hero with an elixir.
- Heroic boss: about 24% for a maxed hero, 38% with an elixir.
- Classes: every matchup between 46% and 52% in duels. Bigger class bonuses were tried and rejected because they pushed some matchups to 35/65.
- Card level 4 and 5 upgrades were rejected because they made the boss a 92 to 96% win.

## Consequences

- Balance changes come with numbers in the pull request, not opinions.
- The simulator isn't kept in the repository; it is rebuilt when needed from the same pattern.
