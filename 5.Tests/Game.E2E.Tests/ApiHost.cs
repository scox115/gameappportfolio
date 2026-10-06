using Game.Api.Caching;
using Game.Api.Messaging;
using Game.Api.Workers;
using Game.Core.Battles;
using Game.Core.Interfaces;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Game.E2E.Tests;

// The real API on a real port, so the browser can call it and open SignalR connections.
// Like the API tests: an in-memory database, no RabbitMQ, fake blob storage and player-friendly dice.
public sealed class ApiHost : WebApplicationFactory<global::Program>
{
    private readonly string _clientOrigin;
    private readonly string _databaseName = $"browser-tests-{Guid.NewGuid()}";

    public ApiHost(int port, string clientOrigin)
    {
        _clientOrigin = clientOrigin;
        BaseUrl = $"http://127.0.0.1:{port}";
        UseKestrel(port);
    }

    public string BaseUrl { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused-in-browser-tests");
        builder.UseSetting("ConnectionStrings:AzureBlobStorage", "UseDevelopmentStorage=true");
        builder.UseSetting("Cors:AllowedOrigins:0", _clientOrigin);
        // Every browser comes from 127.0.0.1, so lift the per-address limits.
        builder.UseSetting("AntiCheat:RegistrationsPerHour", "100000");
        builder.UseSetting("AntiCheat:SignInsPerMinute", "100000");
        builder.UseSetting("AntiCheat:AvatarUploadsPerHour", "100000");
        builder.UseSetting("RabbitMq:UserName", "test");
        builder.UseSetting("RabbitMq:Password", "test");
        builder.UseSetting("Jwt:SigningKey", "browser-tests-signing-key-that-is-long-enough");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");

        builder.ConfigureServices(services =>
        {
            RemoveAll<DbContextOptions<AppDbContext>>(services);
            RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>(services);
            services.AddDbContext<AppDbContext>((sp, options) => options
                .UseInMemoryDatabase(_databaseName)
                .AddOutputCacheEviction(sp));

            services.Remove(services.Single(d => d.ImplementationType == typeof(MatchConsumerWorker)));
            services.Remove(services.Single(d => d.ImplementationType == typeof(MatchTelemetrySender)));

            // Every roll is the minimum: cards always land and the boss always uses a plain Slash,
            // so a hero who keeps casting Fireball wins and every duel ends in a few turns.
            RemoveAll<IBattleRandom>(services);
            services.AddSingleton<IBattleRandom>(new MinimumRolls());

            RemoveAll<IStorageService>(services);
            services.AddSingleton<IStorageService>(new NoStorage());
        });
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    private sealed class MinimumRolls : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => minInclusive;
    }

    private sealed class NoStorage : IStorageService
    {
        public Task<string> UploadFileAsync(Stream fileStream, string fileName, string containerName, string contentType) =>
            Task.FromResult($"https://storage.test/{containerName}/{fileName}");

        public Task DeleteFileAsync(string fileUrl, string containerName) => Task.CompletedTask;
    }
}
