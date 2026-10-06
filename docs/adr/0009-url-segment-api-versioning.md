# 0009. Version the API in the URL

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

The client is downloaded once and can sit in a browser tab for hours, so an API change can't assume every client updates at the same moment.

## Decision

Every game route lives under `/api/v1`, using `Asp.Versioning`. Each response lists the supported versions. The old unversioned `/api` routes stay as aliases of v1 until 2027-04-01 and answer with `Deprecation`, `Sunset` and `Link` headers (RFC 9745 and RFC 8594). Swagger shows one document per version. Health checks and SignalR hubs stay unversioned because they have their own contracts. Details are in [api-versioning.md](../api-versioning.md).

## Alternatives considered

- **Header or media-type versioning.** Cleaner URLs, but harder to see in logs, to try in a browser and to cache.
- **No versioning until needed.** Retrofitting it later breaks every existing client once.

## Consequences

- A breaking change ships as v2 next to v1.
- An unknown version returns 404 with a problem+json body.
