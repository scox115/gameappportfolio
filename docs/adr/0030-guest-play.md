# 0030. Let people play as a guest, and keep the hero later

- **Status:** Accepted
- **Date:** 2026-10-07
- **Builds on:** [0005](0005-identity-jwt-refresh-tokens.md) (sessions), [0020](0020-account-export-and-deletion.md) (deletion) and [0024](0024-moderation-and-reports.md) (hero names)

## Context

The game opened on a sign-up form. Someone following a link from a CV or a portfolio wants to see it working in a few seconds, and choosing a name and a password first is enough to make many of them leave. The game's free tiers also mean the first visit after a quiet spell is already slow while the API and database wake up, so every step before the first battle counts.

## Decision

- **One click starts a guest hero.** `POST /api/v1/auth/guest` creates an account with no password and a made-up name (`Guest` and six random digits, checked against the name rules and existing heroes), starts a hero of the chosen class and returns a normal session. The refresh token is the only way back in, so a guest plays in the browser that started it and nowhere else.
- **A guest plays everything except what other players would notice.** Boss fights, the shop, bounties and friendly duels work as usual. Guests can't wager gold in duels and are left off both leaderboards, so a stream of throwaway heroes can't farm a pot or fill the rankings. A guest's duels still move their rating, but duels between two players on the same network already count as practice and move nothing (`AntiCheat:SameNetworkDuelsArePractice`), so a guest in a second tab can't feed rating to a real hero.
- **Keeping the hero is one form.** In town, a guest sees what they're missing and a "Keep this hero" form. `POST /api/v1/players/me/keep` checks the name like a sign-up, then saves the new name, the password and the hero together: nothing changes if the name is taken or the password is too weak. The hero keeps its gold, cards, level and history, and opponents' match history shows the new name.
- **Abandoned guests are deleted.** The cleanup worker deletes a guest once it was started more than `Cleanup:GuestRetentionDays` (1) ago and has no refresh token that still works. With 7-day refresh tokens that is about a week after the guest was last played, and after that nobody could ever sign in to it again. Each is deleted exactly as a player deleting their own hero would be ([0020](0020-account-export-and-deletion.md)), so nothing is left behind; a guest in the middle of a duel waits for the next run.
- **Starting guests is rate-limited per address**, `AntiCheat:GuestsPerHour` (20), as sign-ups are, so a script can't fill the database with them.
- **Guests don't see the account settings** (password, recovery email, two-factor, export and delete), which all need a password. A guest who wants their hero gone can simply stop playing.

## Alternatives considered

- **A demo mode that runs in the browser with no account.** Nothing to clean up, but it would need a second copy of the battle rules in the client, and the point of the game is that the server plays every battle ([0002](0002-server-authoritative-battles.md)). A guest uses exactly the code a signed-up player does.
- **One shared demo account.** Simple, but everyone would see each other's gold and progress, and one visitor could spoil it for the next.
- **Let guests wager and appear on leaderboards.** Fewer differences to explain, but anyone could make guests to feed gold to their real hero, or crowd the leaderboard with made-up names.
- **Keep guest heroes forever.** Nothing is lost if someone comes back, but they can't come back without the session, so the rows would only grow.

## Consequences

- Someone can be in a boss fight with one click, and keep their progress if they like the game.
- Guest rows exist for about a week after their last play. The cleanup runs every six hours, so the database never holds more than a week's worth of visitors.
- Clearing site data or using a private window loses a guest hero for good; the "Keep this hero" box says so.
- The privacy policy says how long guest heroes are kept.
