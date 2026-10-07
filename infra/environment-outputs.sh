#!/usr/bin/env bash
# Prints the addresses of a deployed copy of the game (staging or production), read from the outputs
# of the last successful Bicep deployment in its resource group, as key=value lines for
# $GITHUB_OUTPUT:
#
#   api=https://...          the API
#   avatars=https://...      the blob storage that serves portraits
#   telemetry=...            the Application Insights connection string (may be empty)
#   swa=swa-...              the Static Web App's name
#
#   environment-outputs.sh <resource group>
#
# Pull request previews use it to find staging (see docs/adr/0026-staging-and-previews.md).
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "Usage: environment-outputs.sh <resource group>" >&2
  exit 2
fi
group=$1

last=$(az deployment group list --resource-group "$group" \
  --query "sort_by([?starts_with(name, 'card-arena-') && properties.provisioningState == 'Succeeded'], &properties.timestamp)[-1].name" \
  --output tsv)
if [ -z "$last" ]; then
  echo "::error::Nothing has been deployed to $group yet. Run the Deploy to Azure workflow first." >&2
  exit 1
fi

outputs=$(az deployment group show --resource-group "$group" --name "$last" --query properties.outputs --output json)
jq -r '
  "api=\(.apiUrl.value)",
  "avatars=https://\(.storageAccount.value).blob.core.windows.net",
  "telemetry=\(.appInsightsConnectionString.value // "")",
  "swa=\(.staticWebAppName.value)"
' <<< "$outputs"
