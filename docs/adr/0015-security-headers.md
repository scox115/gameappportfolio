# 0015. A strict Content Security Policy and security headers on every response

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

Access tokens live in the WebAssembly app's memory ([0005](0005-identity-jwt-refresh-tokens.md)), so a cross-site scripting bug could still read them while the page is open. ADR 0005 named a Content Security Policy as the next defence. The client and API also sent no HSTS header, and the API sent no headers telling browsers what its responses are.

## Decision

**The game client** (Static Web Apps, `globalHeaders` in `wwwroot/staticwebapp.config.json`) sends:

```
default-src 'self';
script-src 'self' 'wasm-unsafe-eval' 'sha256-<import map>';
style-src 'self' 'unsafe-inline';
img-src 'self' data: <avatar storage>;
connect-src 'self' <API> <API WebSocket>;
object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

- `'wasm-unsafe-eval'` lets the .NET runtime compile WebAssembly without allowing JavaScript `eval`.
- `dotnet publish` writes one inline `<script type="importmap">` whose content changes with every build, so it is allowed by its SHA-256 hash rather than `'unsafe-inline'`.
- The API and avatar addresses differ per environment. They are placeholders filled in at deploy time by `infra/set-client-csp.py` (now `infra/configure-client.py`), and the deploy fails if the live site doesn't send the policy.
- Plus HSTS for a year, `nosniff`, `X-Frame-Options: DENY`, a referrer policy, a permissions policy and `Cross-Origin-Opener-Policy: same-origin`.

**The API** (`Game.Api/Security/SecurityHeaders.cs`) adds to every response, errors included: `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (it only returns JSON), `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` and a permissions policy. Outside Development it sends HSTS for a year, after `UseForwardedHeaders` so requests that reached Azure over HTTPS count. Kestrel no longer sends a `Server` header.

**Tests.** The Playwright host serves the published client with the same headers, filled in the same way, so every browser test (accessibility scans included) runs under the real policy. `ContentSecurityPolicyTests` records every `securitypolicyviolation` event while it uploads a portrait, visits each screen, fights the boss and starts a duel, and fails on any. Removing the WebSocket origin from the policy makes it fail, so it detects a real block.

## Alternatives considered

- **`'unsafe-inline'` for scripts.** Simpler, but it would undo most of the protection.
- **Nonces.** They need a server that renders each page; Static Web Apps serves static files.
- **Dropping `'unsafe-inline'` for styles.** The Razor components use many `style` attributes. Moving them into CSS classes is a large change, and injected styles are far less dangerous than injected scripts.
- **`includeSubDomains` and preload on HSTS.** The hosts are subdomains Azure owns, and preload can't be undone quickly.

## Consequences

- An injected `<script>`, inline event handler or call to an unknown server is blocked by the browser.
- A new external resource (a CDN font, analytics, another API) must be added to the policy, or the browser tests fail.
- Local runs (Aspire or `dotnet run`) are served by the Blazor dev server and don't send the policy. The browser tests do.
