# 0001. Clean Architecture with a framework-free domain

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

The game's rules (card damage, boss behaviour, rewards, Elo rating, shop prices, anti-cheat limits) change often while balancing, and every change must be easy to test. The project must also show how a production .NET service is organised.

## Decision

The solution is split by dependency direction:

| Project | Depends on | Holds |
| --- | --- | --- |
| `1.Core/Game.Core` | nothing | Entities (`Player`, `PveBattle`, `PvpBattle`), rules (`MatchRulesEngine`, `EloRating`, `GoldShop`, `DuelRewardRules`), domain events, and interfaces such as `IStorageService` and `IBattleRandom` |
| `2.Infrastructure/Game.Infrastructure` | Core | EF Core `AppDbContext` and configurations, migrations, Identity user, Azure Blob storage, the match history projector |
| `3.BackendAPI/Game.Api` | Core, Infrastructure | Minimal API endpoints, SignalR hubs, auth, workers, caching, health, telemetry |
| `4.Frontend/Game.Client` | nothing (talks HTTP and SignalR) | Blazor WebAssembly UI |

Entities guard their own state. Gold, XP and battle state only change through methods such as `Player.DeductGold` or `PveBattle.PlayCard`, which enforce the rules, so no endpoint can put a player into an invalid state. Randomness and time come in through `IBattleRandom` and `TimeProvider`, so rules are deterministic under test.

## Alternatives considered

- **One web project.** Faster to start, but rules would mix with EF Core and HTTP code and every rule test would need a database or web host.
- **Full CQRS with MediatR.** More ceremony than a game of this size needs, and MediatR moved to a commercial licence. Minimal API endpoints call the domain directly.

## Consequences

- 132 Core tests run in about a second with no database, web host or mocks.
- The client has its own copies of the DTOs instead of referencing Core. That keeps the WebAssembly download small and the domain off the client, at the cost of keeping the two in step (the API tests catch drift).
- EF Core configurations live in Infrastructure, so entities keep private setters and backing fields that EF Core is configured to fill, a small concession to the ORM.
