# Architecture Decision Records

Each record explains one decision that shapes this codebase: what problem it solved, what was chosen, what was turned down, and what it costs. They follow Michael Nygard's [ADR format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions). A record is never rewritten after the fact. If a decision changes, a new record supersedes the old one and says so.

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-clean-architecture.md) | Clean Architecture with a framework-free domain | Accepted |
| [0002](0002-server-authoritative-battles.md) | The server plays every battle; the client only picks cards | Accepted |
| [0003](0003-rewards-synchronous-messaging-for-read-models.md) | Pay rewards in the request, use RabbitMQ only for read models | Accepted, partly superseded by 0023 |
| [0004](0004-optimistic-concurrency.md) | Optimistic concurrency on players and battles | Accepted |
| [0005](0005-identity-jwt-refresh-tokens.md) | ASP.NET Core Identity with short JWTs and rotating refresh tokens | Accepted |
| [0006](0006-single-replica-signalr.md) | Real-time duels on SignalR with an in-memory lobby, one replica | Accepted |
| [0007](0007-free-tier-azure-hosting.md) | Host on Azure free tiers, scaling to zero | Accepted |
| [0008](0008-passwordless-azure-access.md) | No stored Azure credentials: managed identity and GitHub OIDC | Accepted |
| [0009](0009-url-segment-api-versioning.md) | Version the API in the URL | Accepted |
| [0010](0010-output-caching-with-tag-eviction.md) | Output caching evicted by EF Core saves, not Redis | Accepted |
| [0011](0011-feature-flags.md) | Feature flags in configuration, with optional Azure App Configuration | Accepted |
| [0012](0012-testing-strategy.md) | A test pyramid that runs on every pull request | Accepted |
| [0013](0013-aspire-for-local-orchestration.md) | .NET Aspire for local runs, Docker Compose kept | Accepted |
| [0014](0014-simulation-driven-balance.md) | Tune game balance with simulations, not guesses | Accepted |
| [0015](0015-security-headers.md) | A strict Content Security Policy and security headers on every response | Accepted |
| [0016](0016-blue-green-deploys.md) | Blue-green releases with Container Apps revisions | Accepted |
| [0017](0017-restore-drills.md) | Monthly restore drills for the game database | Accepted |
| [0018](0018-status-page.md) | A public status page backed by the API | Accepted |
| [0019](0019-browser-telemetry.md) | Browser telemetry with the Application Insights JavaScript SDK | Accepted |
| [0020](0020-account-export-and-deletion.md) | Players can download their data and delete their account | Accepted |
| [0021](0021-admin-roles-and-audit-log.md) | Admin roles from configuration, with an audit log of every admin action | Accepted |
| [0022](0022-account-recovery-by-email.md) | Account recovery by email with Azure Communication Services | Accepted |
| [0023](0023-transactional-outbox.md) | Match events go through a transactional outbox | Accepted |

To add one, copy [template.md](template.md), give it the next number, and add it to the table.
