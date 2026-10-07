using System.Runtime.CompilerServices;
using Game.Core.Entities;
using Game.Core.History;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Game.Api.Caching;

// Caches the read-heavy responses that are the same for every player (leaderboards, arena stats,
// the class list), so a busy town screen doesn't query SQL on every visit. Saving a change to the
// data behind a response evicts it at once (see CacheEvictionInterceptor), so nobody waits out the
// expiry to see their new rating; the expiry only covers changes made outside the API.
//
// The cache lives in the API's memory, which is right while the API runs as a single instance.
// Setting ConnectionStrings:Redis moves it to Redis, so several instances share one cache.
public static class OutputCaching
{
    public static class Policies
    {
        public const string Leaderboard = "leaderboard";
        public const string PlayerCount = "player-count";
        public const string ArenaStats = "arena-stats";
        public const string Classes = "classes";
        public const string Status = "status";
    }

    public static class Tags
    {
        /// <summary>Responses built from Players (and their class records).</summary>
        public const string Players = "players";

        /// <summary>Responses built from DailyArenaStats.</summary>
        public const string ArenaStats = "arena-stats";
    }

    public static IServiceCollection AddGameOutputCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOutputCache(options =>
        {
            options.AddPolicy(Policies.Leaderboard, policy => policy
                .AddPolicy<SameForEveryonePolicy>()
                .SetVaryByQuery("class")
                .Tag(Tags.Players)
                .Expire(TimeSpan.FromMinutes(1)), excludeDefaultPolicy: true);

            options.AddPolicy(Policies.PlayerCount, policy => policy
                .AddPolicy<SameForEveryonePolicy>()
                .Tag(Tags.Players)
                .Expire(TimeSpan.FromMinutes(1)), excludeDefaultPolicy: true);

            options.AddPolicy(Policies.ArenaStats, policy => policy
                .AddPolicy<SameForEveryonePolicy>()
                .SetVaryByQuery("days")
                .Tag(Tags.ArenaStats)
                .Expire(TimeSpan.FromMinutes(1)), excludeDefaultPolicy: true);

            // The classes only change with a deploy, which starts a new process and an empty cache.
            options.AddPolicy(Policies.Classes, policy => policy
                .AddPolicy<SameForEveryonePolicy>()
                .Expire(TimeSpan.FromHours(1)), excludeDefaultPolicy: true);

            // The status page: at most two health checks a minute however many people are watching.
            options.AddPolicy(Policies.Status, policy => policy
                .AddPolicy<SameForEveryonePolicy>()
                .Expire(TimeSpan.FromSeconds(30)), excludeDefaultPolicy: true);
        });

        if (configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            services.AddStackExchangeRedisOutputCache(options =>
            {
                options.Configuration = redis;
                options.InstanceName = "card-arena:";
            });
        }

        services.AddSingleton<CacheEvictionInterceptor>();
        return services;
    }

    /// <summary>Evicts cached responses when the data behind them is saved; add it to the DbContext.</summary>
    public static DbContextOptionsBuilder AddOutputCacheEviction(this DbContextOptionsBuilder options, IServiceProvider services) =>
        options.AddInterceptors(services.GetRequiredService<CacheEvictionInterceptor>());

    // The built-in default policy won't cache a request that carries an Authorization header, because
    // the response might be personal. The responses these policies cover are the same for everyone,
    // and the client sends its token on every call, so this policy caches them regardless.
    // Never use it on an endpoint whose response depends on who is asking.
    private sealed class SameForEveryonePolicy : IOutputCachePolicy
    {
        public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            var method = context.HttpContext.Request.Method;
            var cacheable = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

            context.EnableOutputCaching = true;
            context.AllowCacheLookup = cacheable;
            context.AllowCacheStorage = cacheable;
            // When many players miss at once, one request queries SQL and the rest wait for its answer.
            context.AllowLocking = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

        public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            var response = context.HttpContext.Response;
            // Errors and validation problems aren't worth keeping, and a cookie would be personal.
            if (response.StatusCode != StatusCodes.Status200OK || response.Headers.SetCookie.Count > 0)
            {
                context.AllowCacheStorage = false;
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// After a save that changed players or arena stats, evicts the cached responses built from them.
/// It runs for every save in the API process, including the match-history consumer's.
/// </summary>
public sealed class CacheEvictionInterceptor(IOutputCacheStore store, ILogger<CacheEvictionInterceptor> logger) : SaveChangesInterceptor
{
    // Which tags each in-flight save touches; noted before saving, because afterwards every entry is Unchanged.
    private readonly ConditionalWeakTable<DbContext, string[]> _pending = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Note(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Note(eventData.Context);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        await EvictAsync(eventData.Context);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        EvictAsync(eventData.Context).AsTask().GetAwaiter().GetResult();
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private void Note(DbContext? context)
    {
        if (context is null) return;

        var tags = context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity switch
            {
                Player or ClassRecord => OutputCaching.Tags.Players,
                DailyArenaStats => OutputCaching.Tags.ArenaStats,
                _ => null
            })
            .OfType<string>()
            .Distinct()
            .ToArray();

        _pending.AddOrUpdate(context, tags);
    }

    private void Forget(DbContext? context)
    {
        if (context is not null) _pending.Remove(context);
    }

    private async ValueTask EvictAsync(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var tags)) return;
        _pending.Remove(context);

        foreach (var tag in tags)
        {
            try
            {
                // Not cancellable: the save has happened, so a stale response must not survive it.
                await store.EvictByTagAsync(tag, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A cache outage (Redis down) shouldn't fail a save that already succeeded; the expiry catches up.
                logger.LogWarning(ex, "Couldn't evict cached responses tagged {Tag}", tag);
            }
        }
    }
}
