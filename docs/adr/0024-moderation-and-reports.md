# 0024. Moderate hero names and portraits with name rules, player reports and admin actions

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

Hero names show on the public leaderboard, in duels and in other players' match history, and portraits show next to them. Until now any name of 3 to 50 characters was accepted and any PNG or JPEG could be a portrait, so one player could put a slur in front of everyone, and admins ([0021](0021-admin-roles-and-audit-log.md)) had no way to hear about it or fix it short of suspending the account.

A public game needs three things: stop the obvious cases at sign-up, let players flag what gets through, and let an admin fix it without throwing the player out.

## Decision

- **Name rules at sign-up** (`Game.Core/Moderation/HeroNames.cs`). A name is 3 to 50 characters of letters, digits, spaces and `_ - . '`, with at least one letter. It is checked against a short block list after folding look-alikes (accents removed, `0→o`, `1→i`, `3→e`, `4→a`, `5→s`, `7→t`, `8→b`, `9→g`) and squashing repeated letters, so `Sh1iiit` is caught. Words that hide inside innocent ones (`ass` in Classic, `tit` in Title) are only blocked as whole words, split on spaces, symbols and camelCase. Names that start with or contain staff words (`Admin…`, `Moderator…`, `GM`, `Support`, `Official`) are refused so nobody can pose as staff.
- **Player reports** (`PlayerReport`). A signed-in player can report another hero's name or portrait, with an optional note of up to 200 characters, from the leaderboard or the duel screen. Reporting the same thing twice while it is open does nothing, and each player can file 10 reports an hour (`AntiCheat:ReportsPerHour`). The reported hero is never told who reported them.
- **A report queue for admins.** `GET /api/v1/admin/reports` groups open reports by hero and reason, with the number of reports, when they came in and the newest notes. Reporters aren't named, so the admin judges the name, not the people.
- **Three admin actions**, each with a required reason and recorded in the audit log in the same save, like the actions in 0021:
  - **Rename** gives the hero a name that passes the same rules. The old name is kept (`PreviousNormalizedUserName`) so the player can still sign in with it, opponents' match history shows the new name, and the open name reports are closed as acted on.
  - **Remove portrait** deletes the blob and closes the portrait reports.
  - **Dismiss reports** closes reports that turned out to be fine.
- **Retention.** Closed reports are deleted 90 days after an admin deals with them (`Cleanup:ResolvedReportRetentionDays`). Open ones are kept until someone looks. A player's data export includes the reports they filed, and deleting a hero deletes reports by and about them.

## Alternatives considered

- **Azure AI Content Safety.** It scores text and images for hate, sexual and violent content and would catch portraits too. It has a free tier (5,000 calls a month), but it adds a second Azure resource and a network call to every sign-up, and it is tuned for sentences, not short game handles. A word list catches what matters for names; Content Safety is the natural next step for portraits if reports show they are a problem.
- **A large open-source word list.** Lists with thousands of entries block many ordinary names (the "Scunthorpe problem") and need constant tuning. A short list, with whole-word matching for risky fragments, keeps false positives low; the tests include names like Classic Knight, The Therapist and Badminton Ace that must pass. The few words blocked anywhere in a name can still catch a real place or surname (Scunthorpe is the classic case); that player picks another name or asks for help.
- **Let players rename themselves.** Useful later, but it makes names harder to trace in reports and history. An admin rename with a reason in the audit log is enough for moderation.
- **Hide reported content automatically after N reports.** Easy to abuse: a few accounts could hide anyone's portrait. Every change goes through an admin.

## Consequences

- Most offensive names are stopped before anyone sees them, and the rest can be reported in two clicks and fixed in one, with a reason on record.
- A word list is never complete, and new spellings will get through. They get reported, renamed, and added to the list.
- Existing names aren't checked again. An admin can rename any that were created before the rules.
- A renamed player can sign in with either name, so a rename never locks anyone out. Only the latest previous name is kept; a second rename forgets the first.
