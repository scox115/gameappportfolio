using Game.Api.Caching;
using Game.Api.Hubs;
using Game.Api.Messaging;
using Game.Api.Workers;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.Interfaces;
using Game.Core.Seasons;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
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
    public static readonly string[] AdminNames = ["RefereeFlow", "RefereeAxe", "RefereeJudge", "RefereeNew"];

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
        builder.UseSetting("AntiCheat:GuestsPerHour", "100000");
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
            services.Remove(services.Single(d => d.ImplementationType == typeof(MatchOutboxRelay)));

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

    /// <summary>
    /// Turns on two-factor sign-in straight in the database, so an admin can use the admin tools in tests that
    /// aren't about setting two-factor up.
    /// </summary>
    public async Task TurnOnTwoFactorAsync(string username)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.SetTwoFactorEnabledAsync((await users.FindByNameAsync(username))!, true);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    /// <summary>Puts a hero at the top of the leaderboard, so a test can find them there among every other test's heroes.</summary>
    public async Task SetRatingAsync(string username, int rating)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = await db.Players.SingleAsync(p => p.Username == username);
        db.Entry(player).Property(p => p.Rating).CurrentValue = rating;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// As if the hero had just won a match worth this rating: the live lobby tells open leaderboards to
    /// reload, the way it does after a real match.
    /// </summary>
    public async Task RecordMatchAsync(string username, int rating)
    {
        await SetRatingAsync(username, rating);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = await db.Players.SingleAsync(p => p.Username == username);
        db.Matches.Add(new GameMatch(player.Id, GameMatch.AiBossId));
        await db.SaveChangesAsync();
        Services.GetRequiredService<ArenaPulse>().Changed();
    }

    /// <summary>
    /// As if the hero had just fought these duels in the season under way and finished on this rating,
    /// telling open leaderboards to reload the way a real duel does.
    /// </summary>
    public async Task RecordSeasonDuelsAsync(string username, int wins, int losses, int rating)
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var player = await db.Players.SingleAsync(p => p.Username == username);
            player.EnterSeason(Season.At(DateTime.UtcNow));
            for (var i = 0; i < wins; i++) player.RecordPvpWin(0);
            for (var i = 0; i < losses; i++) player.RecordPvpLoss(0);
            await db.SaveChangesAsync();
        }
        await RecordMatchAsync(username, rating);
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
