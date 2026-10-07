# 0021. Grant admin roles from configuration and audit every admin action

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

Running a live game means sometimes acting on a player's account: stopping a cheater, giving back gold a bug
took, or taking away gold an exploit created. Until now the only way to do that was a SQL query against the
production database, which is slow, easy to get wrong, and leaves no record of who changed what or why.

Whatever replaces it must be safe in three ways: only trusted people can use it, nobody can quietly make
themselves an admin through the game, and every action can be checked later, including by the player it
affected.

## Decision

- **Roles.** ASP.NET Core Identity roles, with one role: `Admin`. Access tokens carry the account's roles in a
  `role` claim, and every `/api/v1/admin` endpoint requires the `Admin` policy. The API checks this, not the
  browser: the admin page only hides itself.
- **Who is an admin is configuration.** The `Admin:Usernames` setting (comma-separated) lists the admins. In
  Azure it comes from the `ADMIN_USERNAMES` repository variable through Bicep. At every sign-in,
  `AdminRoleSync` grants the role to listed accounts and removes it from accounts no longer listed. No
  endpoint can grant the role, so a compromised admin account can't create more admins.
- **What admins can do.** Search players by name, suspend an account (1 to 3,650 days, or until reinstated),
  reinstate it, and correct gold by up to 100,000 either way. Every action needs a reason. Admins can't
  suspend themselves or another admin.
- **Suspension is separate from lockout.** Identity's lockout is for failed passwords and clears itself after
  five minutes; a suspension has its own columns (`SuspendedUntil`, `SuspensionReason`). Suspending clears
  the account's session and revokes its refresh tokens, so its access token stops working on the next
  request, and the session hub tells any open browser at once. Refused requests carry
  `X-Session-Ended: suspended`, so the browser says why. Signing in shows the reason and the end date, but
  only after the right password, so knowing a name isn't enough to read it.
- **Audit log.** Every admin action, and every role change made by configuration, adds a row to `AuditLog`
  (who, to whom, what, why, when) in the same `SaveChanges` as the change, so neither can happen without the
  other. Rows are never updated. The log has no foreign key to the player, so it survives account deletion,
  and the cleanup worker deletes rows after 365 days (`Cleanup:AuditLogRetentionDays`). A player's data export
  includes the decisions about their account, without naming the admin.

## Alternatives considered

- **Keep using SQL.** No code to write, but no reasons, no record, and a typo can damage every account.
- **An endpoint for admins to grant the role.** Convenient, but anyone who takes over one admin account
  could then make more. Configuration changes go through GitHub, which has its own access control and history.
- **Entra ID app roles for admins.** The right choice for a team with a company directory; here it would mean
  a second sign-in system for one person, when the game already has accounts.
- **Reuse Identity's lockout for suspensions.** One column fewer, but a suspended player would see "too many
  failed attempts", and a successful sign-in after five minutes would undo it.
- **Soft-delete or event-source player changes instead of an audit table.** More complete history, but far
  more machinery than three admin actions need.

## Consequences

- Adding or removing an admin needs a change to `ADMIN_USERNAMES` and a deploy, and takes effect at that
  account's next sign-in. A removed admin's current token keeps the role until it expires (15 minutes at most).
- Roles live in the token, so admin endpoints don't read the role tables on each request; the cost is that
  15-minute window above.
- The audit log holds player names for a year after an account is deleted. The privacy policy says so.
- New admin actions (moderation of names and portraits is next) add an `AdminAction` value and reuse the log.
