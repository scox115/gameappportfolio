# 0033. An ops dashboard and alerts kept in code

- **Status:** Accepted
- **Date:** 2026-10-07
- **Builds on:** [0019](0019-browser-telemetry.md) (browser telemetry), [0016](0016-blue-green-deploys.md) (blue-green releases), [0027](0027-scale-out.md) (several replicas)

## Context

The game already sends everything to Application Insights: the API's traces and metrics through OpenTelemetry, the browser's screens, load times and crashes, and the game's own counters, such as battles finished. Two email alerts cover server errors and a crash loop. But answering "is the game healthy, and what are players doing?" meant knowing which blades in the portal to open and writing queries by hand. Nothing showed a release going out, how many players had duelled the Arena Bot, or how fast the free database allowance was being spent.

## Decision

- **One Azure Monitor workbook, deployed by Bicep.** [`infra/ops-dashboard/workbook.json`](../../infra/ops-dashboard/workbook.json) is the dashboard. `main.bicep` fills in the environment's Application Insights, Container App and database IDs and deploys it to each environment, so staging and production each have their own. The deploy summary links to it.
- **It answers the questions in order.** Health first (requests, server errors, the slowest 5% of response times, players signed in, battles), then which endpoints are slow and which errors happen in the API and in players' browsers. Then what players do: battles by kind, new heroes (signed up, started as a guest, kept a guest hero), screens opened and page load times. Then releases: requests by API version, so each blue-green switch shows as one version handing over to the next. Last, the Azure resources: replicas, CPU and memory, and database CPU, including billed vCore seconds against the free offer's monthly 100,000.
- **Duels are measured by opponent.** `game.battles.completed` now records every finished duel, with `game.duel.opponent` (`player` or `bot`) and `game.duel.counted`. Practice duels didn't count before, which hid the Arena Bot from the numbers.
- **SignalR connections are left out of response times.** A connection stays open for the whole visit, so its "duration" is a visit length, not a response time.
- **The queries are checked in CI.** Azure accepts a workbook whatever its queries say, and a broken one only shows as an error on a chart. [`check-workbook.cs`](../../infra/ops-dashboard/check-workbook.cs) parses every query with Microsoft's Kusto parser (`Microsoft.Azure.Kusto.Language`) against the Application Insights tables it reads, so a typo or a missing column fails the build. CI also builds and lints the Bicep template.
- **Two more alerts on the same signals**, when `ALERT_EMAIL` is set: the slowest 5% of requests over 2 seconds (ignoring quiet periods of fewer than 20 requests, where a couple of cold starts would trip it), and 10 or more browser errors in 15 minutes. The server can be healthy while a client release is broken for everyone.

## Alternatives considered

- **An Azure portal dashboard (`Microsoft.Portal/dashboards`).** It pins tiles from other blades, but its JSON is verbose and tied to the portal's layout. A workbook mixes queries, metrics and text, follows one time-range picker, and is the format Microsoft's own monitoring templates use.
- **Azure Managed Grafana.** Better charts and familiar to many teams, but the cheapest tier costs money every month, and this project runs on free tiers.
- **Build the dashboard by hand in the portal.** Quickest the first time, but it would exist only in production, drift from the code, and disappear with the resource group. In code it is reviewed, versioned and rebuilt with everything else.

## Consequences

- Anyone with access to the resource group opens one page to see the game's health, its players and its releases. The workbook is free; the two new alerts cost about $0.50 a month each.
- A new query has to pass the checker, which knows the columns the dashboard uses. A query on another table or column means adding it to the checker's table list.
- Editing the dashboard in the portal works, but the next deploy that re-applies the Bicep template puts back the version in the repository. Changes made there are copied back with the workbook's Advanced Editor.
