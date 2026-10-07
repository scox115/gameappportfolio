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
using Microsoft.Extensions.Configuration;
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

    /// <summary>Heroes created with these names are admins (one per test, since names are unique).</summary>
    public static readonly string[] AdminNames = ["RefereeFlow", "RefereeAxe"];

    /// <summary>Every email the API "sent", in order; tests open the links in them.</summary>
    public Mailbox Emails { get; } = new();

    /// <summary>Flips feature flags while the API runs, the way Azure App Configuration does.</summary>
    public FeatureSwitches Features { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused-in-browser-tests");
        builder.UseSetting("ConnectionStrings:AzureBlobStorage", "UseDevelopmentStorage=true");
        builder.UseSetting("Cors:AllowedOrigins:0", _clientOrigin);
        builder.UseSetting("Cleanup:Enabled", "false"); // the in-memory database can't bulk delete
        // Every browser comes from 127.0.0.1, so lift the per-address limits.
        builder.UseSetting("AntiCheat:RegistrationsPerHour", "100000");
        builder.UseSetting("AntiCheat:SignInsPerMinute", "100000");
        builder.UseSetting("AntiCheat:AvatarUploadsPerHour", "100000");
        builder.UseSetting("RabbitMq:UserName", "test");
        builder.UseSetting("RabbitMq:Password", "test");
        builder.UseSetting("Jwt:SigningKey", "browser-tests-signing-key-that-is-long-enough");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Admin:Usernames", string.Join(',', AdminNames));
        builder.UseSetting("Email:Provider", "Log");
        builder.UseSetting("Email:ClientBaseUrl", _clientOrigin);
        builder.ConfigureAppConfiguration(configuration => configuration.Add(Features));

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

            RemoveAll<IEmailSender>(services);
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    public sealed class FeatureSwitches : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Set(string feature, bool enabled)
        {
            Data[$"FeatureManagement:{feature}"] = enabled.ToString();
            OnReload();
        }
    }

    public sealed class Mailbox : IEmailSender
    {
        private readonly List<EmailMessage> _sent = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sent) _sent.Add(message);
            return Task.CompletedTask;
        }

        /// <summary>The first link in the newest email to an address, waiting a little for it to arrive.</summary>
        public async Task<string> LatestLinkToAsync(string address)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                EmailMessage? latest;
                lock (_sent) latest = _sent.LastOrDefault(m => m.To == address);
                if (latest is not null)
                {
                    return System.Text.RegularExpressions.Regex.Match(latest.PlainText, @"https?://\S+").Value;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException($"No email reached {address}.");
        }

        public int CountTo(string address)
        {
            lock (_sent) return _sent.Count(m => m.To == address);
        }
    }

    private sealed class MinimumRolls : IBattleRandom
    {
        public int Next(int minInclusive, int maxExclusive) => minInclusive;
    }

    private sealed class NoStorage : IStorageService
    {
        public Task<string> UploadFileAsync(Stream fileStream, string fileName, string containerName, string contentType) =>
            Task.FromResult($"{ClientHost.AvatarOrigin}/{containerName}/{fileName}");

        public Task DeleteFileAsync(string fileUrl, string containerName) => Task.CompletedTask;
    }
}
