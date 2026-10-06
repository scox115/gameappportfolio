# Local development

Everything runs on free tooling: the .NET 10 SDK and Docker.

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

The API applies EF Core migrations on startup. Create a hero with **Register** (passwords need 8+ characters with upper case, lower case, a digit and a symbol). In Swagger (`/swagger`), call `/api/auth/login`, then paste the `accessToken` into **Authorize** to try the protected endpoints.

### Trying PvP

From the town screen, **Find an Opponent** puts you in the lobby. To play both sides yourself, register two heroes and sign in with each in separate browser windows (one of them private/incognito, so they don't share a session). Moves travel over the SignalR hub at `/hubs/arena`. Each turn has a 30-second timer; a player who runs out of time, or leaves, loses.

The lobby queue is kept in the API's memory, so it assumes one API instance. Scaling out needs a shared queue and an Azure SignalR Service backplane.

### Match history and arena stats (RabbitMQ)

When a battle ends, the API saves the rewards, then publishes a `MatchCompletedEvent` (who fought, as which class, what each hero earned, how it ended) to the durable `match-completed-queue`. `MatchConsumerWorker` reads it and writes the read models behind **Recent Matches** on the town screen (`GET /api/players/me/matches`) and the stats strip on the leaderboard (`GET /api/arena/stats?days=7`):

- `MatchHistory`: one row per hero per match. A unique index on (match, hero) means a message RabbitMQ delivers twice is only counted once.
- `DailyArenaStats`: one row per UTC day, with a concurrency token so two API instances can't overwrite each other's counts.

Both are written in one `SaveChanges`, and the message is acknowledged only afterwards. If the database is unavailable, the event is requeued and retried up to five times. The consumer never changes gold, XP or ratings. If RabbitMQ is down, battles still work: events wait in the API's memory and appear in history once the broker is back. Open http://localhost:15672 (guest/guest) to watch the queue.

### Health checks and monitoring

- `GET /health/live` answers 200 while the API process is running; it checks nothing else (for restart probes).
- `GET /health/ready` checks the database, blob storage and RabbitMQ. It returns 503 only when the database is unreachable. Blob storage or RabbitMQ being down shows as `Degraded` with a 200, because the game still works without avatar uploads or match history.
- Traces, metrics and logs go out over OpenTelemetry. Open the dashboard at http://localhost:18888 to see each request's trace, the `game.battles.completed` and `game.telemetry.*` counters, and structured logs. Without `OTEL_EXPORTER_OTLP_ENDPOINT` set, nothing is exported.
- Errors come back as [problem details](https://www.rfc-editor.org/rfc/rfc9457) JSON with a `traceId` you can search for in the dashboard.

## Where settings live

| Setting | Base (`appsettings.json`) | Development | Secret? |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | none | user-secrets | yes |
| `ConnectionStrings:AzureBlobStorage` | none | `UseDevelopmentStorage=true` (Azurite) | in cloud |
| `Jwt:SigningKey` | none | user-secrets | yes |
| `Jwt:Issuer` / `Audience` | set | inherited | no |
| `Jwt:AccessTokenMinutes` / `RefreshTokenDays` | `15` / `7` (the client renews access tokens with a one-time refresh token) | inherited | no |
| `RabbitMq:HostName` / `Port` / `VirtualHost` | `localhost` / `5672` / `/` | inherited | no |
| `RabbitMq:UserName` / `Password` | none | `guest` / `guest` (RabbitMQ's local default) | in cloud |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | none (no export) | `http://localhost:4317` (the dashboard container) | no |
| `Cors:AllowedOrigins` | empty (no cross-origin calls) | the client's local URLs | no |
| Client `ApiBaseUrl` | empty (same origin) | `http://localhost:5005` | no |
| Client `IdleTimeoutMinutes` | `15` (sign out after this long without activity) | inherited | no |

Hosted environments supply the same keys as environment variables (for example `RabbitMq__Password`, `ConnectionStrings__DefaultConnection`) or from Azure Key Vault. The API refuses to start if a required value is missing.
