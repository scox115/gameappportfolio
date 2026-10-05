dotnet ef migrations add AddPlayerAvatar --project 2.Infrastructure/Game.Infrastructure/Game.Infrastructure.csproj --startup-project 3.BackendAPI/Game.Api/Game.Api.csproj

dotnet ef database update --project 2.Infrastructure/Game.Infrastructure/Game.Infrastructure.csproj --startup-project 3.BackendAPI/Game.Api/Game.Api.csproj
