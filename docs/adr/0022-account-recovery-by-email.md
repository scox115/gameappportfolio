# 0022. Recover accounts by email with Azure Communication Services and one-time links

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

A player who forgot their password had no way back in: the game never asked for an email address, so the
only fix was a person editing the database. Any recovery path is also an attack path, so it has to resist
account takeover, inbox flooding and finding out which heroes exist, and it must keep working when the API
scales to zero and restarts between the email being sent and the link being clicked.

## Decision

- **Email is optional and confirmed.** A player adds an address under *Your account and data*, with their
  password. It's stored only after they click the link sent to it. Reset links go only to a confirmed address,
  and the game uses it for nothing else.
- **Reset by hero name.** *Forgot your password?* asks for the hero name and always answers 202 with the same
  empty body, whether or not the hero exists or has an address.
- **Our own one-time links, not Identity's tokens.** Links carry a 32-byte random token. The `AccountTokens`
  table stores only its SHA-256, the purpose, the user and an expiry: an hour for resets, a day for
  confirmations. A token works once, issuing a new one retires the older ones, and confirming a new address
  retires reset links sent to the old one. Identity's built-in tokens are protected with Data Protection keys,
  which a container that scales to zero would lose and regenerate, so links would break; storing hashes
  matches how refresh tokens already work (ADR 0005), needs no key storage, and is single-use by design.
- **A reset signs out everywhere.** It sets the new password and changes the security stamp, revokes every
  refresh token, clears the session (the session hub tells open browsers) and clears any lockout from failed
  guesses, all in one save.
- **Limits.** Per IP address, `AntiCheat:RecoveryRequestsPerHour` (10). Per account, two emails of the same
  kind at least `Email:ResendCooldown` (2 minutes) apart. Changing or removing the address needs the password
  and counts toward the lockout.
- **Sending.** `IEmailSender` lives in Core. In Azure it's Azure Communication Services with an Azure-managed
  sender domain, reached with the API's managed identity, which has **Communication and Email Service Owner** on
  the resource: no connection string. Locally, `Email:Provider=Log` writes each email to the console. With no
  provider (`None`), the endpoints return 503 and `/api/v1/features` reports `accountRecovery: false`, so the
  client hides the links. The deploy turns it on with the `EMAIL_RECOVERY` repository variable.
- **Links stay out of telemetry and history.** The reset and confirm pages read the token, then replace the URL
  without it. The browser telemetry strips query strings from every URL it sends.

## Alternatives considered

- **ASP.NET Core Identity's token providers.** Less code, but they need Data Protection keys persisted
  somewhere shared, and the tokens stay valid until the security stamp changes rather than working once.
- **SendGrid, Mailgun or SMTP.** Each needs an API key or password to store and rotate. Communication
  Services is in the same subscription and takes the managed identity.
- **A custom sender domain.** Better deliverability and branding, but it needs DNS records (SPF, DKIM) on a
  domain we control. It can be linked later without code changes; only `Email:Sender` changes.
- **Reset by email address.** Two heroes can share an address, and an address lookup leaks more than a
  name lookup. The hero name is what players already type.
- **Security questions.** Weak, guessable, and a second secret to store.

## Consequences

- Recovery is only as safe as the player's inbox; that's the usual trade-off, and the email says what to do
  if they didn't ask.
- Sending happens during the request, so a forgot-password call for a hero with an address can take slightly
  longer than one without. The difference is a single queued API call and the response is the same, so we
  accept it rather than add a background queue.
- Azure-managed domains have low sending limits and can land in spam. Fine at this scale, and the page says
  to check the spam folder.
- The privacy policy now covers the optional email. The export includes it, and deleting the hero deletes it
  and every link.
