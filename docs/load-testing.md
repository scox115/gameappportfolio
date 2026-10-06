# Load testing

How many players can one API replica handle? The load test answers that with [Grafana k6](https://k6.io/) (free and open source). It runs against a production-like copy of the API on your own machine or a GitHub runner. It never runs against the live game.

## What it does

Every k6 virtual user (VU) is one player, and the test runs two kinds side by side:

- **Boss fighters** register a hero and look at the town screen (profile, leaderboard, recent matches). Then they fight the boss over the REST API, playing a card about every 1.2 seconds, and repeat. They use the same strategy as the API tests: Dragon Claw when it is ready, Fireball while it recharges, and Holy Shield when the next hit would be fatal.
- **Duelists** connect to the SignalR arena hub over a WebSocket, using the same JSON protocol as the Blazor client. They queue for a friendly duel and play Fireball when it is their turn.

`loadtest/compose.yaml` starts the same container image the Azure deployment runs. It holds the image to the size of its Container Apps replica (0.5 CPU, 1 GiB) and runs SQL Server, RabbitMQ and Azurite beside it, so match history is written through RabbitMQ just as it is in production. Only one setting differs from production: the per-address sign-up and sign-in limits are lifted, because every simulated player comes from one machine.

A run passes only if it meets all of these thresholds:

| Threshold | Limit |
|---|---|
| Failed game requests | under 1% |
| Game request time, p95 | under 500 ms |
| Duel turn round trip (card sent to update received), p95 | under 500 ms |
| Failed duels | under 5% |
| Checks | over 99% pass |

## Results (2026-10-06)

All runs used the 0.5 CPU / 1 GiB API container and the `load` profile: a 1-minute ramp, 3 minutes at peak, then a ramp down. The 400-player row used the `stress` profile instead, which adds players in three steps over 6 minutes.

| Players (boss + duel) | Requests/s | Median | p95 | p99 | Duel turn p95 | Errors | Result |
|---|---|---|---|---|---|---|---|
| 60 (40 + 20) | 30 | 7 ms | 18 ms | 107 ms | 38 ms | 0% | ✅ pass |
| 200 (150 + 50) | 103 | 11 ms | 203 ms | 586 ms | 285 ms | 0% | ✅ pass |
| 300 (220 + 80) | 129 | 93 ms | 324 ms | 4.3 s | 422 ms | 0% | ✅ pass, but straining |
| up to 400 (300 + 100) | 109 | 207 ms | 803 ms | 1.4 s | 1.2 s | 0% | ❌ too slow |

**What this means:**

- **One replica comfortably handles about 200 players who are all fighting at once.** At 60 players, almost every request takes under 20 ms.
- **The ceiling is about 300 players.** Nothing fails even at 400, but requests start to queue. At 300 players the median jumps from 11 ms to 93 ms, the slowest 1% take over 4 seconds, and duelists wait up to 17 seconds to be paired. That pattern fits the API running out of its half CPU (inferred from the slowdown with no errors; CPU was not profiled).
- Every player in this test is busy all the time. A real player reads the screen, browses the shop and waits for the boss's move, so real traffic per player is lower.

**Why the live game will differ** (the first two points are inferred, not measured):

- In Azure, SQL runs in Central US and the API runs in East US 2 (see PR #39), so each database query adds a cross-region round trip. Expect higher median times live, even at low load.
- The Azure SQL free offer is serverless and pauses when idle. The first request after a pause waits for it to wake up, which is the known cold start.
- The API can't simply add replicas, because the PvP lobby lives in memory (`maxReplicas: 1`). Going past one replica needs a shared lobby (for example in SQL or Redis) and an Azure SignalR Service backplane.

**Why the test doesn't run against the live game:**

- The sign-up limit (3 an hour per address) would stop it.
- It would fill the leaderboard with test heroes.
- It would use up part of the SQL free offer's monthly vCore-seconds allowance.

## Running it

### On GitHub

Go to **Actions**, choose **Load test**, then **Run workflow**. Pick a profile, and optionally set the number of players. The results table appears on the run page, and `summary.json` is attached as an artifact. Pull requests that change `loadtest/` run the 30-second `smoke` profile automatically.

### On your PC

You need Docker Desktop and the .NET SDK. Run these in Windows PowerShell 5.1 from the repository folder:

```powershell
# 1. Build the API's container image (the same one Azure runs)
dotnet publish 3.BackendAPI/Game.Api -c Release -t:PublishContainer -p:ContainerRepository=card-arena-api -p:ContainerImageTags=loadtest -p:ContainerFamily=noble-chiseled-extra

# 2. Run the test (starts SQL Server, RabbitMQ, Azurite and the API, then k6)
$env:PROFILE = "smoke"     # smoke, load or stress; also $env:BOSS_VUS and $env:DUEL_VUS
docker compose -f loadtest/compose.yaml run --rm k6

# 3. Stop everything and delete the test database
docker compose -f loadtest/compose.yaml down -v
```

The results are written to `loadtest/results/summary.md` and `summary.json`. This stack uses port 5005, so stop the API if it is already running from Visual Studio.

To use the script with your own setup, give k6 the API address in `BASE_URL`. The API must have `AntiCheat:RegistrationsPerHour` lifted, or sign-ups will be refused after the third one.
