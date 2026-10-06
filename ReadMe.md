[![CI](https://github.com/scox115/gameappportfolio/actions/workflows/ci.yml/badge.svg)](https://github.com/scox115/gameappportfolio/actions/workflows/ci.yml)

**Run it locally:** with Docker running, `dotnet run --project 6.Aspire/Game.AppHost` starts the database, storage, message broker, API and client, and opens the .NET Aspire dashboard. See [docs/local-development.md](docs/local-development.md).

**Deploy it to Azure:** see [docs/azure-deployment.md](docs/azure-deployment.md).

**Load testing:** one API replica (0.5 CPU, 1 GiB) handles about 200 players fighting at once with a p95 of 203 ms; see [docs/load-testing.md](docs/load-testing.md).

**Security checks:** CI fails on any NuGet package with a known vulnerability, Dependabot opens weekly update PRs for NuGet packages and GitHub Actions, and CodeQL scans the C# code once the repository is public.

========================================================================
🤖 AI COLLABORATION RESUME CHECKLIST: CLOUD-BACKED CARD ARENA APPS
========================================================================
Please adopt the role of a Senior .NET Architect. We are building a portfolio-grade game app called "Kings of the Card Arena" using an XML-based solution format (.slnx) and Clean Architecture folder boundaries. 

The application utilizes a FREE, 100% open-source local cloud infrastructure stack running in Docker (Port 1433: SQL Server, Port 10000: Azurite Storage, Port 5672: RabbitMQ Broker) completely decoupled from paid enterprise packages (MassTransit has been stripped due to v9 licensing rules).

CURRENT STATE ARCHITECTURE CHECKLIST:
1. [Core Project] Implements rich domain entities (Player, GameMatch) and an isolated MatchRulesEngine referee service with zero database or web dependencies.
2. [Infrastructure Project] Hosts Entity Framework Core DbContext mapped via Fluent API configurations to active Docker containers. Implements IStorageService utilizing native Azure.Storage.Blobs SDK streaming models.
3. [Backend API Project] Exposes lightweight Minimal API route groupings (PlayerEndpoints, MatchEndpoints) with CORS unlocked for local host traffic. Implements a native Microsoft.Extensions.Hosting BackgroundService (MatchConsumerWorker) that asynchronously pulls match telemetry packets off RabbitMQ channels. Gold and XP rewards are applied synchronously on the API request thread, never by the worker.
4. [Frontend Blazor WASM] Hosts a unified single-page full-screen game UI client (Index.razor) driven entirely by a Scoped GameState state container service, eliminating slow browser URL parameter navigation reroutes. Features an active login/registration menu overlay, multi-part avatar binary cloud upload forms, and a responsive card battle loop canvas.

GOAL LOGPOINT: 
Our full-stack pipeline builds with zero errors, connects to SQL, stores blobs in Azurite, and successfully publishes/consumes matching events over free RabbitMQ.

Please acknowledge you understand this exact structural footprint, and ask me what feature (e.g., Ranked Leaderboards or xUnit Combat Math Test Engines) we are building next.
========================================================================
