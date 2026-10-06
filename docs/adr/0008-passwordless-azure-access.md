# 0008. No stored Azure credentials: managed identity and GitHub OIDC

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

Connection strings with passwords and long-lived deployment keys are the most common way cloud apps leak. A portfolio repository that may become public must hold no secrets that open Azure.

## Decision

- **The API runs as a user-assigned managed identity.** It reaches Azure SQL, Blob Storage, Key Vault and App Configuration with Entra ID tokens.
- **Azure SQL accepts Entra ID sign-ins only.** The admin is an Entra group holding the developer and the API identity. The storage account has shared-key access disabled.
- **GitHub Actions signs in with OpenID Connect.** A federated credential trusts only this repository's `production` environment, and the deploy app is scoped to one resource group.
- The JWT signing key and RabbitMQ password go from GitHub secrets into Key Vault; the container app reads them through Key Vault references.
- `infra/setup.ps1` does the one-time Entra and GitHub setup, and runs on Windows PowerShell 5.1.

## Alternatives considered

- **SQL authentication and a service principal secret in GitHub.** Simpler to set up, but both are long-lived passwords to rotate and protect.

## Consequences

- There is nothing in the repository or its settings that can be used to sign in to Azure.
- Local development still uses SQL authentication against the Docker container, so the connection string differs per environment. Configuration handles that ([local development](../local-development.md#where-settings-live)).
