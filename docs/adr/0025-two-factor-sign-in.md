# 0025. Two-factor sign-in with an authenticator app and recovery codes

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

A hero's account holds gold, upgrades and rating, and admins' accounts can suspend players and rename heroes ([0021](0021-admin-roles-and-audit-log.md), [0024](0024-moderation-and-reports.md)). Until now a password was the only thing standing between a stranger and an account. Passwords get reused and leaked, so production games and most hiring managers expect a second step to be available.

## Decision

- **Authenticator apps (TOTP, RFC 6238).** A player turns it on from **Your account and data**: they confirm their password, scan a QR code (or type the key) into any authenticator app, and type the 6-digit code it shows. Only then is two-factor on. The QR code is drawn in the browser as an SVG with [QRCoder](https://github.com/codebude/QRCoder), so the secret never goes to a third-party service and the Content Security Policy ([0015](0015-security-headers.md)) needs no change.
- **Sign-in stays one request.** `POST /auth/login` takes an optional `twoFactorCode`. When the password is right and two-factor is on but no code was sent, the API answers 401 with `twoFactorRequired: true`, and the browser shows a code box under the password. Nothing is said about two-factor unless the password was right, so it can't be used to probe accounts.
- **A code works once.** Our own small TOTP checker (`Game.Core/Security/Totp.cs`, tested against the RFC's vectors) records the 30-second step of the last code accepted, so a code someone watched being typed can't be replayed (RFC 6238 section 5.2). It accepts one step either side of now for phones whose clocks are a little off. Two sign-ins racing with the same code are settled by Identity's concurrency stamp: only one saves.
- **Guessing is locked out.** A wrong code counts as a failed sign-in, and five lock the account for five minutes, the same as wrong passwords. Identity only keeps the count when it sees two-factor is really on, which needs an authenticator token provider registered; a test fails without it.
- **Recovery codes.** Turning it on shows 10 one-time codes (like `K7M2Q-XP4RT`) once. Each signs in in place of the app. Only SHA-256 hashes are stored; the codes are random enough (about 50 bits) that a salt adds nothing. The player can get a new set, which retires the old one.
- **Turning it off needs the password and a code**, so neither a stolen password nor a stolen phone is enough on its own.
- **Admins can turn it off** for a player who lost both their phone and their codes, with a reason in the audit log. The admin screen asks how they checked it's really them, such as through the player's recovery email.
- **Data.** The authenticator key sits where Identity keeps it (`AspNetUserTokens`), next to the recovery code hashes. The data export says whether two-factor is on but leaves out the key and the hashes, and deleting the hero deletes both.

## Alternatives considered

- **Codes by email or SMS.** Email is already the recovery channel ([0022](0022-account-recovery-by-email.md)), so using it for the second step too would make one inbox enough to take an account. SMS costs money per message and is open to SIM swapping. Authenticator apps are free and work offline.
- **Passkeys (WebAuthn).** The strongest option and the likely next step, but it needs JavaScript interop for the browser's credential API, a library for attestation, and a different sign-in flow. TOTP covers the common case with no new services.
- **Identity's built-in authenticator check.** It accepts a code again for as long as it is valid and reads the system clock. Our checker refuses reuse and takes the time from `TimeProvider`, which lets the tests move the clock instead of waiting 30 seconds.
- **A separate "second step" token.** Many sites sign in with the password first and then post the code with a short-lived ticket. Sending the password again with the code keeps the API stateless and needs no ticket store; the password is still in the sign-in form, so the browser just sends both.

## Consequences

- Players who want it get a second step. Nothing changes for anyone else.
- The authenticator key is stored as it is (the API must be able to read it to check codes). Azure SQL encrypts the database at rest. Wrapping the key with ASP.NET Core Data Protection would also need a key ring kept outside the container, so it is left for when there is one.
- A lost phone plus lost recovery codes needs an admin, which is deliberate. Self-service recovery by email would weaken the second step to "has the inbox".
- Admins aren't forced to use two-factor yet. Requiring it for the Admin role is a small follow-up once the owner has set it up.
