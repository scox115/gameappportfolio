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
using Microsoft.Extensions.Hosting;

namespace Game.Api.Tests;

// Runs the real API pipeline in memory, with an in-memory database and no RabbitMQ consumer.
public class GameApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"game-api-tests-{Guid.NewGuid()}";

    /// <summary>The API's clock; tests move it forward to run out a turn timer.</summary>
    public TestClock Clock { get; } = new();

    /// <summary>Stands in for blob storage and records what was uploaded and deleted.</summary>
    public FakeStorageService Storage { get; } = new();

    /// <summary>Tops up a player's gold, for tests that need more than a new hero starts with.</summary>
    public async Task GiveGoldAsync(Guid playerId, int amount)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = await db.Players.FindAsync(playerId) ?? throw new InvalidOperationException("No such player.");
        player.AddGold(amount);
        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused-in-tests");
        builder.UseSetting("ConnectionStrings:AzureBlobStorage", "UseDevelopmentStorage=true");
        // Every test client shares one address, so lift the per-address limits (RateLimitTests sets its own).
        builder.UseSetting("AntiCheat:RegistrationsPerHour", "100000");
        builder.UseSetting("AntiCheat:SignInsPerMinute", "100000");
        builder.UseSetting("AntiCheat:AvatarUploadsPerHour", "100000");
        builder.UseSetting("RabbitMq:UserName", "test");
        builder.UseSetting("RabbitMq:Password", "test");
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-that-is-long-enough");

        builder.ConfigureServices(services =>
        {
            // Swap SQL Server for the in-memory provider.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>((sp, options) => options
                .UseInMemoryDatabase(_databaseName)
                .AddOutputCacheEviction(sp));

            // The telemetry sender and consumer need a live broker; they aren't part of what these tests cover.
            services.Remove(services.Single(d => d.ImplementationType == typeof(MatchConsumerWorker)));
            services.Remove(services.Single(d => d.ImplementationType == typeof(MatchTelemetrySender)));

            // Every roll is the minimum, which always favours the player: cards always land and
            // the boss always uses a plain Slash, so battle outcomes are predictable.
            services.RemoveAll<IBattleRandom>();
            services.AddSingleton<IBattleRandom>(new FixedBattleRandom());

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<IStorageService>();
            services.AddSingleton<IStorageService>(Storage);
        });
    }
}

// Lucky rolls the minimum (best for the player); unlucky rolls the maximum (every card fails).
public class FixedBattleRandom(bool lucky = true) : IBattleRandom
{
    public int Next(int minInclusive, int maxExclusive) => lucky ? minInclusive : maxExclusive - 1;
}

public class FakeStorageService : IStorageService
{
    public record StoredFile(string Url, string ContentType, byte[] Content);

    public List<StoredFile> Uploaded { get; } = [];
    public List<string> Deleted { get; } = [];

    public Task<string> UploadFileAsync(Stream fileStream, string fileName, string containerName, string contentType)
    {
        using var copy = new MemoryStream();
        fileStream.CopyTo(copy);
        var url = $"https://storage.test/{containerName}/{Guid.NewGuid()}_{fileName}";
        lock (Uploaded) Uploaded.Add(new StoredFile(url, contentType, copy.ToArray()));
        return Task.FromResult(url);
    }

    public Task DeleteFileAsync(string fileUrl, string containerName)
    {
        lock (Deleted) Deleted.Add(fileUrl);
        return Task.CompletedTask;
    }
}

public class TestClock : TimeProvider
{
    private TimeSpan _offset;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;

    public void Advance(TimeSpan by) => _offset += by;
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }
}
