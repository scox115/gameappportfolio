using Game.Api.Caching;
using Game.Api.Messaging;
using Game.Api.Workers;
using Game.Core.Battles;
using Game.Core.Interfaces;
using Game.Core.Moderation;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Game.Api.Tests;

// Runs the real API pipeline in memory, with an in-memory database and no RabbitMQ consumer.
public class GameApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName;
    private readonly InMemoryDatabaseRoot? _sharedDatabase;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly TestDatabase? _sqlServer;

    public GameApiFactory() : this(null, $"game-api-tests-{Guid.NewGuid()}", new Dictionary<string, string>(), null)
    {
    }

    private GameApiFactory(InMemoryDatabaseRoot? sharedDatabase, string databaseName, IReadOnlyDictionary<string, string> settings, TestDatabase? sqlServer)
    {
        _sharedDatabase = sharedDatabase;
        _databaseName = databaseName;
        _settings = settings;
        _sqlServer = sqlServer;
    }

    /// <summary>
    /// Two API replicas sharing one database, as in Azure with more than one replica: hub messages pass
    /// between them through the HubMessages table (see docs/adr/0027-scale-out.md). Given a SQL Server
    /// database, both use it, and the first to start creates it with the migrations, as in Azure.
    /// </summary>
    public static (GameApiFactory East, GameApiFactory West) TwoReplicas(TestDatabase? sqlServer = null)
    {
        var database = new InMemoryDatabaseRoot();
        var name = $"game-api-replicas-{Guid.NewGuid()}";
        var settings = new Dictionary<string, string>
        {
            ["ScaleOut:Backplane"] = "Sql",
            ["ScaleOut:PollInterval"] = "00:00:00.050"
        };
        return (new GameApiFactory(database, name, settings, sqlServer), new GameApiFactory(database, name, settings, sqlServer));
    }

    /// <summary>The API's clock; tests move it forward to run out a turn timer.</summary>
    public TestClock Clock { get; } = new();

    /// <summary>Stands in for blob storage and records what was uploaded and deleted.</summary>
    public FakeStorageService Storage { get; } = new();

    /// <summary>Stands in for Azure Communication Services and keeps every email "sent".</summary>
    public FakeEmailSender Email { get; } = new();

    /// <summary>Stands in for Azure AI Content Safety: allows every portrait unless a test says otherwise.</summary>
    public FakePortraitScreen Portraits { get; } = new();

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
        builder.UseSetting("AntiCheat:GuestsPerHour", "100000");
        builder.UseSetting("AntiCheat:SignInsPerMinute", "100000");
        builder.UseSetting("AntiCheat:AvatarUploadsPerHour", "100000");
        // The in-memory database can't bulk delete; DataCleanupTests runs the cleanup on SQLite instead.
        builder.UseSetting("Cleanup:Enabled", "false");
        builder.UseSetting("RabbitMq:UserName", "test");
        builder.UseSetting("RabbitMq:Password", "test");
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-that-is-long-enough");
        builder.UseSetting("Email:Provider", "Log");
        builder.UseSetting("Email:ClientBaseUrl", FakeEmailSender.ClientBaseUrl);
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            // Swap SQL Server for the in-memory provider (or a throwaway SQL Server database).
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>((sp, options) =>
            {
                if (_sqlServer is not null) _sqlServer.Configure(options);
                else options.UseInMemoryDatabase(_databaseName, _sharedDatabase);
                options.AddOutputCacheEviction(sp);
            });

            // The outbox relay and the consumer need a live broker; they aren't part of what these tests cover.
            services.Remove(services.Single(d => d.ImplementationType == typeof(MatchConsumerWorker)));
            services.Remove(services.Single(d => d.ImplementationType == typeof(MatchOutboxRelay)));

            // Every roll is the minimum, which always favours the player: cards always land and
            // the boss always uses a plain Slash, so battle outcomes are predictable.
            services.RemoveAll<IBattleRandom>();
            services.AddSingleton<IBattleRandom>(new FixedBattleRandom());

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<IStorageService>();
            services.AddSingleton<IStorageService>(Storage);

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);

            services.RemoveAll<IPortraitScreen>();
            services.AddSingleton<IPortraitScreen>(Portraits);
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

public class FakeEmailSender : IEmailSender
{
    public const string ClientBaseUrl = "https://play.test";

    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add(message);
        return Task.CompletedTask;
    }

    public List<EmailMessage> To(string address)
    {
        lock (Sent) return Sent.Where(m => m.To == address).ToList();
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

public class FakePortraitScreen : IPortraitScreen
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PortraitScreening> _verdicts = new();

    /// <summary>How many images were screened.</summary>
    public int Screened;

    /// <summary>Makes this exact image get this verdict; any other image is allowed.</summary>
    public void Judge(byte[] image, PortraitScreening screening) => _verdicts[Convert.ToBase64String(image)] = screening;

    public Task<PortraitScreening> ScreenAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Screened);
        return Task.FromResult(_verdicts.GetValueOrDefault(Convert.ToBase64String(image.Span), PortraitScreening.Allowed));
    }
}
