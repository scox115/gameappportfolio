using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Api.Operations;
using Game.Core.Operations;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

public class StatusTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // What Azure Container Apps sets in a revision's container.
    private const string AppName = "ca-cardarena-api";
    private const string DnsSuffix = "orangehill-1234.eastus2.azurecontainerapps.io";
    private const string Revision = "ca-cardarena-api--0000019";
    private const string PublicHost = $"{AppName}.{DnsSuffix}";

    [Fact]
    public async Task Status_IsPublic_AndShowsEveryCheckAndTheBuild()
    {
        using var factory = new GameApiFactory();

        var response = await factory.CreateClient().GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<StatusResponse>(Json))!;
        // The test host has a database but no RabbitMQ or Azurite.
        Assert.Equal("Degraded", status.Status);
        Assert.Equal(["blob-storage", "database", "message-broker"], status.Checks.Select(c => c.Name).Order());
        Assert.Equal("Healthy", status.Checks.Single(c => c.Name == "database").Status);
        Assert.Matches(@"^\d+\.\d+\.\d+", status.Version);
        Assert.Null(status.Revision); // not running in Container Apps
        Assert.Empty(status.Releases);
        Assert.Null(status.LastRestoreDrill);
    }

    [Fact]
    public async Task Status_ShowsTheNewestReleasesAndTheLastRestoreDrill()
    {
        using var factory = new GameApiFactory();
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedAsync(factory, db =>
        {
            for (var run = 1; run <= 7; run++)
            {
                db.OperationsEvents.Add(OperationsEvent.Release($"1.0.{run}+abcdef0123456789{run}", $"ca-api--{run:0000000}", start.AddDays(run)));
            }
            db.OperationsEvents.Add(OperationsEvent.RestoreDrill(false, "The restore or the check of the copy failed.", start.AddDays(2)));
            db.OperationsEvents.Add(OperationsEvent.RestoreDrill(true, "Restored in 18.1 minutes; all 19 tables checked.", start.AddDays(6)));
        });

        var status = (await factory.CreateClient().GetFromJsonAsync<StatusResponse>("/api/v1/status", Json))!;

        Assert.Equal(["1.0.7", "1.0.6", "1.0.5", "1.0.4", "1.0.3"], status.Releases.Select(r => r.Version));
        Assert.Equal("abcdef012345", status.Releases[0].Commit);
        Assert.Equal("ca-api--0000007", status.Releases[0].Revision);
        Assert.True(status.LastRestoreDrill!.Succeeded);
        Assert.Equal("Restored in 18.1 minutes; all 19 tables checked.", status.LastRestoreDrill.Detail);
    }

    [Fact]
    public async Task Status_IsCachedBriefly_SoAnIncidentCrowdDoesNotHammerTheDatabase()
    {
        using var factory = new GameApiFactory();
        var client = factory.CreateClient();

        var first = await client.GetAsync("/api/v1/status");
        var second = await client.GetAsync("/api/v1/status");

        Assert.Null(first.Headers.Age);
        Assert.NotNull(second.Headers.Age);
    }

    [Fact]
    public async Task ARelease_IsRecordedOnce_WhenTheRevisionFirstServesThePublicAddress()
    {
        using var factory = InContainerApps();

        // The blue-green smoke test calls the revision's own address, and probes call the container directly:
        // neither means players are on this revision yet.
        await factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{Revision}.{DnsSuffix}") })
            .GetAsync("/health/ready");
        await factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://100.100.0.12:8080") })
            .GetAsync("/health/live");
        Assert.Empty(await ReleasesAsync(factory));

        var players = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{PublicHost}") });
        await players.GetAsync("/api/v1/classes");
        await players.GetAsync("/api/v1/classes");

        var release = Assert.Single(await ReleasesAsync(factory));
        Assert.Equal(Revision, release.Revision);
        Assert.InRange(release.OccurredAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.Equal(factory.Services.GetRequiredService<BuildInfo>().FullVersion, release.Version);
    }

    [Fact]
    public async Task ARelease_IsNotRecordedTwice_WhenAnotherReplicaGotThereFirst()
    {
        using var factory = InContainerApps();
        await SeedAsync(factory, db => db.OperationsEvents.Add(OperationsEvent.Release("1.0.19", Revision, DateTimeOffset.UnixEpoch)));

        await factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{PublicHost}") })
            .GetAsync("/api/v1/classes");

        Assert.Single(await ReleasesAsync(factory));
        Assert.True(factory.Services.GetRequiredService<ReleaseRecorder>().IsDone);
    }

    [Fact]
    public async Task NoRelease_IsRecorded_OutsideContainerApps()
    {
        using var factory = new GameApiFactory();

        await factory.CreateClient().GetAsync("/api/v1/classes");

        Assert.Empty(await ReleasesAsync(factory));
    }

    private static WebApplicationFactory<Program> InContainerApps() => new GameApiFactory().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("CONTAINER_APP_NAME", AppName);
        builder.UseSetting("CONTAINER_APP_ENV_DNS_SUFFIX", DnsSuffix);
        builder.UseSetting("CONTAINER_APP_REVISION", Revision);
    });

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, Action<AppDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<List<OperationsEvent>> ReleasesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OperationsEvents.Where(e => e.Kind == OperationsEventKind.Release).ToListAsync();
    }
}
