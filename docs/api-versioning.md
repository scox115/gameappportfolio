# API versioning

The game API is versioned in the URL with [Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning) (MIT, from the .NET Foundation):

```
GET /api/v1/players/me
POST /api/v1/battles/pve/{battleId}/turns
```

`3.BackendAPI/Game.Api/Versioning/ApiVersioning.cs` sets it up in one place.

## Why the URL

A version in the URL is easy to see in logs, traces, Swagger and the browser's network tab, and it works with a plain link. The alternatives (a header or a query string) hide the version and are easy to forget. The Blazor client and the load test call `/api/v1/...` directly.

## What every response says

- `api-supported-versions: 1.0` lists the versions the API serves. When a version is retired, it moves to `api-deprecated-versions` first.
- A version that doesn't exist, such as `/api/v2/...` today, is a 404 problem details response, because the version is part of the address.

## The old unversioned routes

Before versioning, routes were `/api/players/me` and so on. They still work and behave exactly like v1 (same sign-in, rate limits and rules), so a browser tab opened before a deploy, or any other old caller, doesn't break. They are hidden from Swagger, and every response from them says it is deprecated:

```
Deprecation: @1791244800                          (RFC 9745: deprecated since 6 October 2026)
Sunset: Thu, 01 Apr 2027 00:00:00 GMT              (RFC 8594: may be removed after this date)
Link: </api/v1/players/me>; rel="successor-version"
```

After the sunset date they can be removed by deleting the `LegacyPrefix` group in `MapGameApi`.

## Not versioned

- `/health/live` and `/health/ready`: their contract is with Azure Container Apps' probes, not with players.
- The SignalR hubs (`/hubs/arena`, `/hubs/session`): the hub protocol negotiates its own version, and hub methods are added rather than changed.

## Adding v2

1. Declare it next to `V1` in `ApiVersioning` (`public static readonly ApiVersion V2 = new(2, 0);`) and add `.HasApiVersion(V2)` to the `/api/v{version:apiVersion}` group.
2. Map only the endpoints that change in v2 with `.MapToApiVersion(V2)`; the rest keep answering for both versions.
3. When v1 is on its way out, mark it with `.HasDeprecatedApiVersion(V1)`, so responses list it in `api-deprecated-versions`.

Swagger gets a document per version automatically (`/swagger/v1/swagger.json`, `/swagger/v2/swagger.json`), with the newest first in the Swagger UI.

`5.Tests/Game.Api.Tests/ApiVersioningTests.cs` checks the v1 routes, the deprecation headers on the old routes, unknown versions, and that Swagger lists only v1 routes.
