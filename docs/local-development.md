# Local development

Everything runs on free tooling: the .NET 10 SDK and Docker.

There are two ways to run the game. Both use the same API and client code and settings:

- **The quick way:** one command, using .NET Aspire. This is described just below.
- **The manual way:** Docker Compose plus two `dotnet run` commands, in steps 1 to 3.

## Quick start with .NET Aspire

With Docker Desktop running, use either of these:

```powershell
dotnet run --project 6.Aspire/Game.AppHost
```

- **In Visual Studio:** right-click **Game.AppHost** (in the `6.Aspire` folder), choose **Set as Startup Project**, then press F5.

This one command does all of the following:

- starts SQL Server, Azurite and RabbitMQ in Docker;
- waits until they are ready;
- starts the API on http://localhost:5005 once `/health/ready` passes;
- starts the client on http://localhost:5091;
- opens the [Aspire dashboard](https://aspire.dev/dashboard/overview/). Every service is listed there with its logs, traces (one request can be followed from the browser through the API into SQL and RabbitMQ) and metrics, and you can stop or restart each one.

Nothing needs setting up first:

- **Passwords:** the SQL Server and RabbitMQ passwords and the JWT signing key are generated on the first run, and kept in the AppHost's user-secrets.
- **Data:** heroes, avatars and queued messages live in the Docker volumes `card-arena-sql`, `card-arena-azurite` and `card-arena-rabbitmq`. These are separate from the Docker Compose volumes.
- **Containers:** they keep running after you stop the AppHost, so the next start is quick. Stop them in Docker Desktop, or with `docker ps` and `docker stop`.

**Things to check:**

- Stop anything else that is using ports 5005 or 5091 first, such as the API started from Visual Studio.
- If the dashboard warns "No trusted development certificate", run `dotnet dev-certs https --trust` once.

`6.Aspire/Game.AppHost/AppHost.cs` describes the whole setup in about 50 lines. The API keeps reading the settings names it already uses (`ConnectionStrings:DefaultConnection`, `RabbitMq:*`, `Jwt:SigningKey`), so Docker Compose and Azure work unchanged. `5.Tests/Game.AppHost.Tests` checks this wiring without starting anything.

## 1. Start the local services

```bash
cp .env.example .env          # then edit MSSQL_SA_PASSWORD
docker compose up -d          # SQL Server :1433, Azurite :10000, RabbitMQ :5672, telemetry dashboard :18888
```

Data lives in named Docker volumes (`sql_data`, `azurite_data`, `rabbit_data`), so it survives `docker compose down` and restarts. `docker compose down -v` deletes it.

If you use Visual Studio, make sure its built-in Azurite isn't also running on port 10000 (`netstat -ano | findstr :10000` should show only Docker); otherwise uploads go to that copy instead of the container.

## 2. Give the API its secrets

Secrets are kept out of the repo with [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets). Use the same password you put in `.env`:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=localhost,1433;Database=GameDb;User Id=sa;Password=<your MSSQL_SA_PASSWORD>;TrustServerCertificate=True;" \
  --project 3.BackendAPI/Game.Api
```

The API also needs a key to sign login tokens (any random string of 32+ characters):

```bash
# macOS / Linux
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)" --project 3.BackendAPI/Game.Api
```

```powershell
# Windows PowerShell 5.1 or PowerShell 7
$bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String($bytes)) --project 3.BackendAPI/Game.Api
```

## 3. Run

```bash
dotnet run --project 3.BackendAPI/Game.Api --launch-profile http     # API on http://localhost:5005
dotnet run --project 4.Frontend/Game.Client --launch-profile http    # client on http://localhost:5091
```

The API applies EF Core migrations on startup. Create a hero with **Register** (passwords need 8+ characters with upper case, lower case, a digit and a symbol). In Swagger (`/swagger`), call `/api/v1/auth/login`, then paste the `accessToken` into **Authorize** to try the protected endpoints.

### Trying PvP

From the town screen, **Find an Opponent** puts you in the lobby. To play both sides yourself, register two heroes and sign in with each in separate browser windows (one of them private/incognito, so they don't share a session). Moves travel over the SignalR hub at `/hubs/arena`. Each turn has a 30-second timer; a player who runs out of time, or leaves, loses.

The lobby queue is kept in the API's memory, so it assumes one API instance. Scaling out needs a shared queue and an Azure SignalR Service backplane.

### Match history and arena stats (RabbitMQ)

When a battle ends, the API saves the rewards, then publishes a `MatchCompletedEvent` (who fought, as which class, what each hero earned, how it ended) to the durable `match-completed-queue`. `MatchConsumerWorker` reads it and writes the read models behind **Recent Matches** on the town screen (`GET /api/v1/players/me/matches`) and the stats strip on the leaderboard (`GET /api/v1/arena/stats?days=7`):

- `MatchHistory`: one row per hero per match. A unique index on (match, hero) means a message RabbitMQ delivers twice is only counted once.
- `DailyArenaStats`: one row per UTC day, with a concurrency token so two API instances can't overwrite each other's counts.

Both are written in one `SaveChanges`, and the message is acknowledged only afterwards. If the database is unavailable, the event is requeued and retried up to five times. The consumer never changes gold, XP or ratings. If RabbitMQ is down, battles still work: events wait in the API's memory and appear in history once the broker is back. Open http://localhost:15672 (guest/guest) to watch the queue.

### Health checks and monitoring

- `GET /health/live` answers 200 while the API process is running; it checks nothing else (for restart probes).
- `GET /health/ready` checks the database, blob storage and RabbitMQ. It returns 503 only when the database is unreachable. Blob storage or RabbitMQ being down shows as `Degraded` with a 200, because the game still works without avatar uploads or match history.
- Traces, metrics and logs go out over OpenTelemetry. Open the dashboard at http://localhost:18888 to see each request's trace, the `game.battles.completed` and `game.telemetry.*` counters, and structured logs. Without `OTEL_EXPORTER_OTLP_ENDPOINT` set, nothing is exported.
- Errors come back as [problem details](https://www.rfc-editor.org/rfc/rfc9457) JSON with a `traceId` you can search for in the dashboard.

### Caching

The leaderboards, the player count, the arena stats and the class list are the same for every player, so the API caches them with [output caching](https://learn.microsoft.com/aspnet/core/performance/caching/output) instead of querying SQL on every visit (`3.BackendAPI/Game.Api/Caching/OutputCaching.cs`). They are cached even for signed-in players, which the built-in policy would refuse, because none of them depends on who is asking. Personal endpoints such as `/players/me` are never cached.

A cached response stays fresh because saving a change evicts it: an EF Core interceptor notices when a save touches `Players` or `DailyArenaStats` and evicts the responses tagged with them, so a new rating shows on the leaderboard straight away. Each entry also expires after a minute (an hour for the class list), which covers changes made outside the API, such as an edit in SSMS. When lots of players miss at once, one request queries SQL and the rest wait for its answer. A response served from the cache carries an `Age` header.

The cache lives in the API's memory, which is enough for one instance. To share it between several instances, run Redis (`docker run -d -p 6379:6379 redis:8-alpine`) and set `ConnectionStrings:Redis` (for example `localhost:6379`). Azure doesn't use Redis yet, because it has no free tier and the API runs as one instance.

## 4. Test

```bash
dotnet test GamePortfolioSolution.slnx --filter "Category!=Browser"   # unit and API tests, a few seconds
dotnet test 5.Tests/Game.E2E.Tests                                      # browser tests, about a minute
```

The browser tests (`5.Tests/Game.E2E.Tests`) play the game in Chromium with [Playwright](https://playwright.dev/dotnet/): they create heroes, sign in again, beat the boss, fight a friendly duel between two browsers, and check that signing in on a second browser signs the first one out. `AccessibilityTests` scans every screen with [axe-core](https://github.com/dequelabs/axe-core) against WCAG 2.1 AA (contrast, labels, alt text, ARIA) and plays a whole boss fight with the keyboard alone, so a new problem fails the build. They need no Docker services: the tests publish the Blazor client, serve it on a free port, and start the real API on another with an in-memory database, fake blob storage, no RabbitMQ and dice that always favour the player. The first run downloads Chromium (about 150 MB). To watch it play, set `HEADED=1` first (`$env:HEADED = "1"` in PowerShell). When a test fails, a screenshot of each browser is saved to `bin/<configuration>/net10.0/screenshots`. In Visual Studio they show in Test Explorer under the `Browser` trait.

CI runs them on every pull request in the `browser-tests` job, and uploads the screenshots with the results when something fails.

## Where settings live

| Setting | Base (`appsettings.json`) | Development | Secret? |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | none | user-secrets | yes |
| `ConnectionStrings:AzureBlobStorage` | none | `UseDevelopmentStorage=true` (Azurite) | in cloud: the blob endpoint URL, reached with managed identity |
| `Jwt:SigningKey` | none | user-secrets | yes |
| `Jwt:Issuer` / `Audience` | set | inherited | no |
| `Jwt:AccessTokenMinutes` / `RefreshTokenDays` | `15` / `7` (the client renews access tokens with a one-time refresh token) | inherited | no |
| `ConnectionStrings:Redis` | none (cache in API memory) | none | yes, if set |
| `RabbitMq:HostName` / `Port` / `VirtualHost` | `localhost` / `5672` / `/` | inherited | no |
| `RabbitMq:UserName` / `Password` | none | `guest` / `guest` (RabbitMQ's local default) | in cloud |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | none (no export) | `http://localhost:4317` (the dashboard container) | no |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | none (no export) | none | set by the Azure deployment |
| `ForwardedHeaders:TrustAllProxies` | `false` | `false` | `true` only behind Container Apps' ingress |
| `Cors:AllowedOrigins` | empty (no cross-origin calls) | the client's local URLs | no |
| Client `ApiBaseUrl` | empty (same origin) | `http://localhost:5005` | no |
| Client `IdleTimeoutMinutes` | `15` (sign out after this long without activity) | inherited | no |

Hosted environments supply the same keys as environment variables (for example `RabbitMq__Password`, `ConnectionStrings__DefaultConnection`) or from Azure Key Vault. The API refuses to start if a required value is missing. See [azure-deployment.md](azure-deployment.md) for how the Azure deployment sets them.
