#!/usr/bin/env bash
# Blue-green releases for the API's Container App (see docs/adr/0016-blue-green-deploys.md).
#
# The app runs in "multiple revisions" mode with all traffic pinned to one revision by name, so a
# deploy creates a new revision that gets no players. This script tests the new revision on its own
# address, and only then moves traffic to it. The previous revision stays active with no traffic,
# scaled to zero, so a rollback is one traffic switch.
#
#   blue-green.sh live     <resource group> <app>              prints the revision serving players
#   blue-green.sh release  <resource group> <app> <new> <old>  tests <new>, then moves traffic to it
#   blue-green.sh rollback <resource group> <app> [revision]   moves traffic back (default: the previous revision)
#
# Settings (environment): SMOKE_ATTEMPTS (default 30) and SMOKE_DELAY seconds (default 10).
set -euo pipefail

SMOKE_ATTEMPTS="${SMOKE_ATTEMPTS:-30}"
SMOKE_DELAY="${SMOKE_DELAY:-10}"

log() { echo "$*" >&2; }

# The revision that has the traffic: pinned by name once blue-green is on, otherwise the latest ready one.
live_revision() {
  local group=$1 app=$2 pinned
  pinned=$(az containerapp ingress traffic show --resource-group "$group" --name "$app" \
    --query "[?weight > \`0\` && revisionName != null].revisionName | [0]" --output tsv)
  if [ -n "$pinned" ]; then
    echo "$pinned"
  else
    az containerapp show --resource-group "$group" --name "$app" \
      --query properties.latestReadyRevisionName --output tsv
  fi
}

# Waits for a revision to be provisioned, then checks it answers on its own address.
# A revision with no traffic is scaled to zero, so the first request also wakes it (and the database).
smoke_test() {
  local group=$1 app=$2 revision=$3 attempt state fqdn
  for attempt in $(seq 1 "$SMOKE_ATTEMPTS"); do
    state=$(az containerapp revision show --resource-group "$group" --name "$app" --revision "$revision" \
      --query properties.provisioningState --output tsv)
    case "$state" in
      Provisioned) break ;;
      Failed) log "::error::Revision $revision failed to provision."; return 1 ;;
    esac
    log "Waiting for revision $revision to provision ($state, attempt $attempt)..."
    sleep "$SMOKE_DELAY"
  done
  if [ "$state" != "Provisioned" ]; then
    log "::error::Revision $revision was still $state after $SMOKE_ATTEMPTS checks."
    return 1
  fi

  fqdn=$(az containerapp revision show --resource-group "$group" --name "$app" --revision "$revision" \
    --query properties.fqdn --output tsv)
  for attempt in $(seq 1 "$SMOKE_ATTEMPTS"); do
    # Ready means the database answers; the classes list is a real API call through EF Core.
    if curl --fail --silent --max-time 20 "https://$fqdn/health/ready" > /dev/null \
      && curl --fail --silent --max-time 20 "https://$fqdn/api/v1/classes" > /dev/null; then
      log "Revision $revision is healthy at https://$fqdn"
      return 0
    fi
    log "Waiting for revision $revision to answer (attempt $attempt)..."
    sleep "$SMOKE_DELAY"
  done
  log "::error::Revision $revision never became healthy at https://$fqdn/health/ready."
  return 1
}

switch_traffic() {
  local group=$1 app=$2 revision=$3
  az containerapp ingress traffic set --resource-group "$group" --name "$app" \
    --revision-weight "$revision=100" --output none
  log "All traffic now goes to $revision."
}

# Keeps only the revisions named; everything else is deactivated so old builds don't pile up.
keep_only() {
  local group=$1 app=$2; shift 2
  local revision keep
  for revision in $(az containerapp revision list --resource-group "$group" --name "$app" \
    --query "[?properties.active].name" --output tsv); do
    for keep in "$@"; do
      if [ "$revision" = "$keep" ]; then continue 2; fi
    done
    az containerapp revision deactivate --resource-group "$group" --name "$app" --revision "$revision" --output none
    log "Deactivated old revision $revision."
  done
}

release() {
  local group=$1 app=$2 new=$3 old=${4:-}
  if [ -n "$old" ] && [ "$new" = "$old" ]; then
    log "Revision $new is already live; nothing to switch."
    return 0
  fi
  if ! smoke_test "$group" "$app" "$new"; then
    # Players never saw it: traffic is still on the old revision.
    az containerapp revision deactivate --resource-group "$group" --name "$app" --revision "$new" --output none
    log "::error::Kept traffic on ${old:-the current revision} and deactivated $new."
    return 1
  fi
  switch_traffic "$group" "$app" "$new"
  if [ -n "$old" ]; then
    keep_only "$group" "$app" "$new" "$old"
    log "Previous revision $old stays active with no traffic, for a quick rollback."
  fi
}

rollback() {
  local group=$1 app=$2 target=${3:-} live
  live=$(live_revision "$group" "$app")
  if [ -z "$target" ]; then
    # The newest other active revision: the one the last release replaced and kept on standby.
    # (A release that failed its smoke test deactivates its revision, so it is never picked.)
    target=$(az containerapp revision list --resource-group "$group" --name "$app" \
      --query "sort_by([?properties.active && name != '$live'], &properties.createdTime)[-1].name" --output tsv)
  fi
  if [ -z "$target" ] || [ "$target" = "$live" ]; then
    log "::error::There is no other revision to roll back to (live: $live)."
    return 1
  fi
  log "Rolling back from $live to $target."
  az containerapp revision activate --resource-group "$group" --name "$app" --revision "$target" --output none
  smoke_test "$group" "$app" "$target"
  switch_traffic "$group" "$app" "$target"
  echo "$target"
}

command=${1:-}
shift || true
case "$command" in
  live) live_revision "$@" ;;
  release) release "$@" ;;
  rollback) rollback "$@" ;;
  *) log "Usage: $0 live|release|rollback <resource group> <app> ..."; exit 2 ;;
esac
