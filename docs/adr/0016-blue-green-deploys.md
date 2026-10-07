# 0016. Blue-green releases with Container Apps revisions

- **Status:** Accepted
- **Date:** 2026-10-07

## Context

Every merge to `main` deploys. Until now the Container App ran in single-revision mode, so a new build took all the traffic the moment it started. A build that passed CI but couldn't start in Azure (a failing migration, a missing setting, a bad image) took the game down until someone fixed it and merged again. The deploy's smoke test only ran after players were already on the new build.

## Decision

- The API's Container App runs in **multiple-revision mode**, with all traffic **pinned by name** to the live revision (`liveRevision` in `infra/main.bicep`). A deploy, whether it is the full Bicep deployment or the faster image swap, creates a new revision that gets no traffic.
- `infra/blue-green.sh release` waits for the new revision to provision. It then calls it on its own revision address (`/health/ready`, then `/api/v1/classes`, which goes through EF Core to the database), retrying while it wakes from zero. Only when it passes does traffic move to it, all at once.
- If it fails, the new revision is deactivated and the run fails. Players never reach it and the game client isn't deployed.
- The revision it replaced **stays active with no traffic**. It scales to zero, so it costs nothing, and the **Roll back the API** workflow (`.github/workflows/rollback.yml`) can return to it in about a minute, after the same smoke test. Older revisions are deactivated.
- The script is tested in CI against stand-ins for `az` and `curl` (`infra/tests/test_blue_green.py`), covering a healthy release, an unhealthy one, a revision stuck provisioning, re-running the same commit, rollback and rollback with nothing to go back to. It is also linted with ShellCheck.

## Alternatives considered

- **Gradual (canary) traffic splitting**, for example 10% then 100%. Container Apps supports it, but the duel lobby lives in memory per revision ([0006](0006-single-replica-signalr.md)), so two revisions sharing players would split the lobby, and players waiting for an opponent might never meet one. All-at-once switching avoids that.
- **Deployment slots on App Service.** The same idea, but they need the Standard tier (about $70 a month).
- **Revision labels** (stable `blue` and `green` addresses). Useful for manual testing, but they add names to keep track of without changing what is checked.

## Consequences

- A broken build costs a failed run, not an outage.
- **Migrations must be backward compatible.** The new revision migrates the database while players are still on the old one, and a rollback runs old code against the new schema. Schema changes are split into expand and contract releases. This is documented in [azure-deployment.md](../azure-deployment.md).
- At the switch, players connected to the old revision keep their connection until it scales down. Duels in progress are saved in SQL, so they carry on. A player waiting in the old revision's lobby has to search again.
- Rolling back moves only the API; the client stays on the newer build. That works when the release only changed the API. If the newer client needs something the older API lacks, fixing forward is the way out, not rollback.
- A deploy takes about a minute longer while the new revision wakes and is tested.
