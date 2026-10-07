# 0019. Browser telemetry with the Application Insights JavaScript SDK

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

The API sends traces, metrics and logs to Application Insights through OpenTelemetry, but the game itself runs in the player's browser. A slow first load, a screen nobody visits, a failed API call or a crash while rendering was invisible unless a player reported it. Blazor WebAssembly downloads a .NET runtime before anything shows, so load time matters more than on most sites.

## Decision

- Use Microsoft's **Application Insights JavaScript SDK** (`@microsoft/applicationinsights-web` 3.4.5), sending to the **same Application Insights resource** as the API. The browser shows up as its own role, `card-arena-client`, on the Application Map, calling `card-arena-api`.
- **Serve the SDK from the site** (`wwwroot/lib/applicationinsights`) instead of Microsoft's CDN, so the Content Security Policy still allows scripts only from `'self'`. `connect-src` gains exactly one origin, the resource's ingestion endpoint. The SDK's CDN settings sync and its own usage stats are turned off, because both would call other Microsoft hosts.
- **What is sent** (`wwwroot/js/telemetry.js`, `Services/BrowserTelemetry.cs`, `Services/TelemetryLoggerProvider.cs`):
  - A page view for each game screen (the game changes screens without changing the URL) and for the status page. The first one carries the page's load timing.
  - Every API call, with its duration and result. The calls carry W3C `traceparent` headers, so each one links to the API request it caused, end to end. Blazor's runtime downloads are excluded.
  - Every error Blazor logs, such as an unhandled exception while rendering, as an exception with its .NET type and stack trace. Unhandled JavaScript errors and promise rejections are also sent.
  - The player's id once they sign in, never their name.
- **No cookies.** With cookies off, the SDK needs no consent banner. The cost is that a "session" lasts one page load and returning visitors aren't counted as returning.
- **Configured at deploy time.** `infra/configure-client.py` puts the connection string and the API's origin into two meta tags in `index.html` and adds the ingestion origin to the policy. Without a connection string, as in local runs, the script does nothing.
- **Tested in the browser.** The browser tests point the client at a stand-in ingestion endpoint (`TelemetrySink`) on its own origin, so every test runs with telemetry on under the real policy. `TelemetryTests` checks the screens, the load timing, the API calls with matching trace ids, the player id and a reported crash.

## Alternatives considered

- **Loading the SDK from Microsoft's CDN.** This is the documented snippet, but it adds a third-party script origin to `script-src`, so a compromise of the CDN would run code in the game.
- **Sending browser telemetry to the API and forwarding it from there.** It hides the connection string and avoids ad blockers, but it is more code to own, it loses the SDK's load timing and dependency correlation, and every item would wake the API.
- **A separate Application Insights resource for the browser.** It keeps browser data apart, but the end-to-end transaction view and the Application Map work best within one resource.

## Consequences

- One place shows the whole path from a player's click to the database, with the same trace id in the browser and the API.
- The connection string is public in `index.html`. It only allows sending telemetry, as on every site that uses the SDK. Someone could send fake data, and the Log Analytics daily cap (0.15 GB) bounds what that could cost.
- The SDK adds about 186 KB (about 65 KB compressed) to the first load, and is cached after that.
- Ad blockers often block Application Insights, so browser numbers undercount. The game works the same either way.
- Upgrading the SDK means replacing the file in `wwwroot/lib/applicationinsights` and its reference in `index.html`, because nothing tracks it the way NuGet tracks packages.
