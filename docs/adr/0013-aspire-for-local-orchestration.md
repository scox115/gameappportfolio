# 0013. .NET Aspire for local runs, Docker Compose kept

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

Running the game locally meant starting SQL Server, Azurite and RabbitMQ with Docker Compose, setting user-secrets, then starting the API and client in two terminals. That is a lot of steps for a reviewer who just wants to see it run.

## Decision

`6.Aspire/Game.AppHost` starts everything with one command (or F5 in Visual Studio), generates the passwords and signing key on first run, waits for each dependency, and opens the Aspire dashboard with logs, traces and metrics. It passes settings using the names the API already reads, so the API has no Aspire-specific code and no ServiceDefaults project; it already has its own health checks and OpenTelemetry. Docker Compose stays as the manual path.

## Alternatives considered

- **Aspire all the way to Azure (`azd`).** The Bicep and GitHub Actions deployment already exists and shows more of the platform.
- **The Aspire dashboard in Azure Container Apps.** Turned down: in preview with no published price and in-memory data. Application Insights covers production telemetry.

## Consequences

- A new contributor needs only Docker and the .NET SDK.
- The client is WebAssembly and can't read environment variables, so the API keeps a fixed local port (5005). A test guards it.
