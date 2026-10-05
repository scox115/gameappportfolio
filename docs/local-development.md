# Local development

Everything runs on free tooling: the .NET 10 SDK and Docker.

## 1. Start the local services

```bash
cp .env.example .env          # then edit MSSQL_SA_PASSWORD
docker compose up -d          # SQL Server :1433, Azurite :10000, RabbitMQ :5672
```

## 2. Give the API its secrets

Secrets are kept out of the repo with [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets). Use the same password you put in `.env`:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=localhost,1433;Database=GameDb;User Id=sa;Password=<your MSSQL_SA_PASSWORD>;TrustServerCertificate=True;" \
  --project 3.BackendAPI/Game.Api
```

The API also needs a key to sign login tokens (any random string of 32+ characters):

```bash
# macOS / Linux
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)" --project 3.BackendAPI/Game.Api
```

```powershell
# Windows PowerShell
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))) --project 3.BackendAPI/Game.Api
```

## 3. Run

```bash
dotnet run --project 3.BackendAPI/Game.Api --launch-profile http     # API on http://localhost:5005
dotnet run --project 4.Frontend/Game.Client --launch-profile http    # client on http://localhost:5091
```

The API applies EF Core migrations on startup. Create a hero with **Register** (passwords need 8+ characters with upper case, lower case, a digit and a symbol). In Swagger (`/swagger`), call `/api/auth/login`, then paste the `accessToken` into **Authorize** to try the protected endpoints.

## Where settings live

| Setting | Base (`appsettings.json`) | Development | Secret? |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | none | user-secrets | yes |
| `ConnectionStrings:AzureBlobStorage` | none | `UseDevelopmentStorage=true` (Azurite) | in cloud |
| `Jwt:SigningKey` | none | user-secrets | yes |
| `Jwt:Issuer` / `Audience` / `AccessTokenMinutes` | set | inherited | no |
| `RabbitMq:HostName` / `Port` / `VirtualHost` | `localhost` / `5672` / `/` | inherited | no |
| `RabbitMq:UserName` / `Password` | none | `guest` / `guest` (RabbitMQ's local default) | in cloud |
| `Cors:AllowedOrigins` | empty (no cross-origin calls) | the client's local URLs | no |
| Client `ApiBaseUrl` | empty (same origin) | `http://localhost:5005` | no |

Hosted environments supply the same keys as environment variables (for example `RabbitMq__Password`, `ConnectionStrings__DefaultConnection`) or from Azure Key Vault. The API refuses to start if a required value is missing.
