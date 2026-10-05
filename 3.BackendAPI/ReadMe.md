dotnet ef migrations add AddPlayerAvatar --project 2.Infrastructure/Game.Infrastructure/Game.Infrastructure.csproj --startup-project 3.BackendAPI/Game.Api/Game.Api.csproj

dotnet ef database update --project 2.Infrastructure/Game.Infrastructure/Game.Infrastructure.csproj --startup-project 3.BackendAPI/Game.Api/Game.Api.csproj

--

# 1. Boot up all cloud emulation containers simultaneously
docker compose up -d

# 2. Push your Entity Framework schemas straight down to SQL Server
dotnet ef database update --project 2.Infrastructure/Game.Infrastructure/Game.Infrastructure.csproj --startup-project 3.BackendAPI/Game.Api/Game.Api.csproj
