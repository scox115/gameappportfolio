# 0031. A smaller client, and wake the server while it loads

- **Status:** Accepted
- **Date:** 2026-10-07
- **Builds on:** [0007](0007-free-tier-azure-hosting.md), which accepted a slow first visit after a quiet spell as the price of scaling to zero

## Context

A first visit has two waits. The browser downloads the Blazor WebAssembly client, and on a quiet day the API has scaled to zero and the serverless database has paused, so the first request waits for both to wake. Until now the client waited for the download to finish before it asked the API for anything, so the two waits ran one after the other, and nothing on screen said why the game was slow.

The download for an English-speaking visitor was 2.64 MB (Brotli-compressed). Two parts of it did nothing for this game:

- **Culture data (ICU), about 140 KB.** It formats dates and numbers in the visitor's language, but every word in the game is English.
- **Unused runtime code.** The .NET WebAssembly runtime ships prebuilt with everything any app might need. Its native module, at 0.93 MB, was the largest file.

## Decision

- **No culture data.** `InvariantGlobalization` is on in `Game.Client.csproj`. Dates and numbers are formatted the same way for everyone (English month names, `1,234`), and times are still shown in the visitor's own time zone.
- **Rebuild the runtime for this app.** CI, the deploys and the pull request previews install the `wasm-tools` workload before publishing. With it, `dotnet publish` relinks the runtime with only what the client uses, and without the culture code, which takes the native module from 0.93 MB to 0.45 MB. Without the workload (a local build) publishing still works and gives the larger runtime.
- **Wake the server as soon as the page opens.** `wwwroot/js/wake.js` runs before the client starts downloading and asks the API's `/health/ready` every few seconds until it answers. That request wakes the API replica and the database, so they start up while the client downloads instead of after it. The API's address reaches the page in an `api-origin` meta tag that `infra/configure-client.py` fills in at deploy time.
- **Say what's happening.** If the server hasn't answered within two seconds, a strip across the top (`Layout/ServerWake.razor`) says it's waking up and counts the seconds; it says when the server is awake, then goes away. After three minutes it says the server isn't answering, links to the status page and offers a retry. An awake server answers at once, so normally nothing shows.

## Results

The same game, measured in Chromium with network throttling, from opening the page to the sign-in screen being ready:

| Connection | Before | After |
| --- | --- | --- |
| Download, compressed | 2.64 MB | 2.01 MB (−24%) |
| 9 Mbps, 60 ms latency | 4.3 s | 3.1 s |
| 1.6 Mbps, 150 ms latency | 18.0 s | 14.5 s |

Waking the server is no faster, but it now overlaps the download, so a visitor on a fast connection waits for whichever is slower, not both added together.

## Alternatives considered

- **Keep a replica always running.** Removes the API's cold start, but costs money every hour, and the database would still pause unless kept awake too ([0007](0007-free-tier-azure-hosting.md)).
- **Ping the API on a schedule to keep it awake.** Same cost as the replica, and it would keep the database from ever pausing.
- **Ahead-of-time (AOT) compilation.** Faster once running, but makes the download larger, the opposite of what a first visit needs.
- **Server-side prerendering.** Would show the page before the client downloads, but the client is hosted as static files on Static Web Apps; prerendering needs a server, and that server would be asleep too.
- **Lazy-loading assemblies** such as the QR code library used only when turning on two-factor sign-in. About 40 KB, which isn't worth the extra code yet.

## Consequences

- First visits are about a quarter faster to download, and a cold start overlaps the download and is explained on screen.
- Visitors whose browser is set to another language see English month names. The game's text is all English anyway.
- Each client publish in CI and the deploys takes longer, by the workload install and the relink.
- `/health/ready` is called by every new visit, a few times while waking. It checks the database and storage, which costs the same as one of the game's own requests.
