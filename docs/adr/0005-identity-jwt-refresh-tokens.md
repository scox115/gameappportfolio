# 0005. ASP.NET Core Identity with short JWTs and rotating refresh tokens

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

The first version signed players in by name only. Accounts hold gold and a public rating, so they need passwords. The client is a Blazor WebAssembly app on a different origin from the API, and SignalR needs the same identity.

## Decision

- **ASP.NET Core Identity** stores users and password hashes in the game database. `ApplicationUser.Id` equals `Player.Id`, so there is no mapping table.
- **Access tokens** are JWTs that live 15 minutes, signed with a key from user-secrets locally and Key Vault in Azure. SignalR receives them as the `access_token` query string.
- **Refresh tokens** are random, stored hashed, single use and rotated on every refresh. Reusing an old one revokes all of that user's tokens, the standard defence against a stolen token.
- **One browser per account.** Each sign-in issues a new session id (`sid` claim). Older sessions are rejected and told instantly over `SessionHub`.
- **Rate limits** on sign-up (3 an hour per IP) and sign-in (10 a minute per IP) use the built-in ASP.NET Core rate limiter.

## Alternatives considered

- **Cookies with the BFF pattern.** Safer against token theft in the browser, but needs a server-side host for the WebAssembly app, which Static Web Apps Free doesn't give.
- **Entra External ID or Auth0.** Production-grade, but a portfolio piece should show how the pieces work, and both add sign-up friction and cost at scale.

## Consequences

- Tokens are kept in the WebAssembly app's memory only, never in browser storage, so a page refresh signs the player out. Cross-site scripting could still reach them while the page is open; the short lifetime and refresh rotation limit the damage, and a strict Content Security Policy is the next step.
- The API is stateless apart from the refresh-token and session tables.
