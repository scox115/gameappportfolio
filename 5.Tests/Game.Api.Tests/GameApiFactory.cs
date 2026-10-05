using Game.Api.Workers;
using Game.Core.Battles;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused-in-tests");
        builder.UseSetting("ConnectionStrings:AzureBlobStorage", "UseDevelopmentStorage=true");
        builder.UseSetting("RabbitMq:UserName", "test");
        builder.UseSetting("RabbitMq:Password", "test");
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-that-is-long-enough");

        builder.ConfigureServices(services =>
        {
            // Swap SQL Server for the in-memory provider.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            // The telemetry consumer needs a live broker; it isn't part of what these tests cover.
            var worker = services.Single(d => d.ImplementationType == typeof(MatchConsumerWorker));
            services.Remove(worker);

            // Boss attacks always roll the minimum so battle outcomes are predictable.
            services.RemoveAll<IBattleRandom>();
            services.AddSingleton<IBattleRandom>(new FixedBattleRandom());
        });
    }
}

public class FixedBattleRandom : IBattleRandom
{
    public int Next(int minInclusive, int maxExclusive) => minInclusive;
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
