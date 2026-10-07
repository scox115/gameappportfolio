# 0029. Screen portraits with Azure AI Content Safety before they're shown

- **Status:** Accepted
- **Date:** 2026-10-07
- **Builds on:** [0024](0024-moderation-and-reports.md), which named Content Safety as the next step for portraits

## Context

A portrait shows next to a hero's name on the leaderboard, in duels and in other players' match history. Since [0024](0024-moderation-and-reports.md), an offensive one can be reported and an admin can take it down, but only after other players have seen it. A word list stops the worst names before anyone sees them; portraits had nothing equivalent.

Azure AI Content Safety rates an image for four kinds of harm (hate, sexual content, self-harm and violence) on a scale of 0 (safe), 2 (low), 4 (medium) and 6 (high). Its free tier allows 5,000 images a month and 5 a second, and accepts images of up to 4 MB, from 50 × 50 to 7,200 × 7,200 pixels.

## Decision

- **Every upload is screened before it's stored.** `POST /api/v1/players/me/avatar` checks the type from the bytes as before, then sends the image to Content Safety. Only an image that passes is saved to Blob Storage and becomes the portrait, so a failed one is never at a URL anyone can open.
- **What blocks a portrait** is in the domain (`Game.Core/Moderation/PortraitScreening.cs`). Hate, sexual content and self-harm block from "low" up. Violence blocks from "medium" up, because this is a fantasy battle game, and a hero holding a sword or facing a dragon can rate as low violence. The player is told which kind of content was found, in a sentence the client shows as it is.
- **If it can't be checked, it doesn't go up.** If the service is down, out of its monthly allowance or refuses the API, the upload gets a 503 asking the player to try again later, and their current portrait stays. If the service can't read the image (smaller than 50 × 50, say), the player is asked for a different one. Two retries and a 10-second timeout keep a player from waiting long.
- **Portraits are capped at 4 MB**, down from 5, which is the most Content Safety accepts.
- **It's switched on per copy of the game.** `ContentSafety:Endpoint` turns it on. The Bicep template creates a free-tier `ContentSafety` resource, with keys switched off and **Cognitive Services User** for the API's managed identity, when the repository variable `CONTENT_SAFETY` is `true`. Without an endpoint (locally, in tests, on staging) every portrait is allowed, which is how things were before. Staging leaves it off, as it does recovery email: test uploads don't need screening, and it keeps staging to the resources every copy needs.
- **Reports stay.** Screening catches what a model can see; players still report portraits that are offensive in ways it can't, such as a picture of a real person used to mock them, and admins still take them down ([0024](0024-moderation-and-reports.md)).

## Alternatives considered

- **Screen after upload, and hide the portrait if it fails.** Players get their portrait at once even when the service is slow, but a bad image is public for that moment, and a failure would need a background job to retry. Screening first is simpler and never shows an unchecked image.
- **Accept the portrait unchecked when the service is down.** Nobody is blocked by an outage, but an outage, or a month's allowance used up, would quietly turn screening off. Changing a portrait can wait; showing an offensive one can't be undone.
- **Block at "low" for every kind of harm.** The strictest setting, but likely to turn away ordinary fantasy art. The violence threshold is one constant, easy to tighten if reports show it is too loose.
- **Run an open-source image classifier in the API.** No per-image cost or outside call, but a model of a useful size wouldn't fit comfortably in a 0.5 CPU, 1 GiB replica, and keeping it accurate becomes this project's job.

## Consequences

- Most offensive portraits are stopped before anyone sees them, and the player is told why.
- The free tier covers 5,000 uploads a month. Each player can upload 10 an hour (`AntiCheat:AvatarUploadsPerHour`), so a handful of determined players could use up the month; after that, portraits can't be changed until it resets. Moving to the Standard tier is a one-line change in `infra/main.bicep` and charges per image.
- An upload takes a little longer, by one call to Content Safety.
- Each portrait is sent to Microsoft for screening, which the privacy policy now says.
- The tests run the real Content Safety client against a stand-in for the service, so the request it sends and how each answer (blocked, unreadable, refused, unreachable) is handled are checked without an Azure resource. Whether the thresholds suit real portraits can only be seen once it's on in production.
