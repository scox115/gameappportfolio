# 0020. Players can download their data and delete their account

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

The game stores personal data: a username, a password hash, sign-in sessions, a portrait, purchases and match history. Privacy laws such as the GDPR give people the right to a copy of their data and the right to have it erased, and players expect both from any real service. Before this change, the only way to remove a hero was to ask someone with database access.

## Decision

- **"Your account and data"** in town opens a dialog with two actions.
- **Download my data** calls `GET /api/v1/players/me/export`, which returns one JSON file, `card-arena-<name>-<date>.json`. It holds the sign-in account, the hero's profile, purchases and records, sign-in sessions (times only), match history, boss fights and duels. Password hashes, token hashes and security stamps are left out: only the server can use them.
- **Delete my hero forever** calls `DELETE /api/v1/players/me` with the player's password.
  - The password check counts toward the sign-in lockout and shares its rate limit, so a stolen session can't delete an account or guess the password.
  - Deletion is refused during a duel, which would otherwise leave the opponent waiting on a hero who no longer exists.
  - The account, the hero (with their purchases, bounties and records), refresh tokens, boss fights, duels, matches and match history are all removed in **one `SaveChanges`**, so an error leaves the account whole. The portrait blob is deleted afterwards.
  - The name is free for a new player immediately, and the deleted player's access tokens stop working on their next request.
- **Opponents keep their history.** A duel is also part of the opponent's record, so their entry stays, with the deleted hero's name replaced by "A retired hero". Daily arena totals are anonymous counts and stay as they are.
- **Late match events.** Match history is built from RabbitMQ events, so an event can arrive after a delete. The projector skips heroes who no longer exist and hides their name from the opponent. If no hero in the match remains, nothing is recorded at all.

## Alternatives considered

- **Soft delete (a "deleted" flag).** It allows an undo window, but the data would still be there, every query would need a filter, and the name would stay taken. A real erasure is simpler to reason about and to prove in tests.
- **Deleting the opponents' history entries too.** That would erase another player's record of a match they played. Hiding the name removes the personal data and keeps their history whole.
- **Emailing the export.** Accounts have no email address, and a download behind the player's own sign-in is simpler and just as private.

## Consequences

- Deleted data can survive in places the API can't reach:
  - the database's point-in-time backups, for up to 7 days;
  - the portrait blob, for 7 days under soft delete;
  - Application Insights, where browser telemetry holds the player's id and the logs hold API activity, until the workspace's 30-day retention removes them.
  
  These limits are stated here so they can be given to a player who asks.
- Restoring a database backup from before a delete would bring the deleted account back. The disaster recovery runbooks say to delete it again after such a restore.
- A delete reads every row it removes. That is fine at this game's size, and it is what keeps the delete in one transaction and testable on the in-memory database.
