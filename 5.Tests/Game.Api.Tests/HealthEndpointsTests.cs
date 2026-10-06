using System.Net;
using System.Text.Json;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Game.Api.Tests;

public class HealthEndpointsTests : IClassFixture<GameApiFactory>
{
    private readonly GameApiFactory _factory;

    public HealthEndpointsTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Live_IsUpWithoutCheckingDependencies()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("checks").EnumerateObject());
    }

    [Fact]
    public async Task Ready_StaysUpWhenOnlyOptionalServicesAreDown()
    {
        // The test host has a database but no RabbitMQ connection and no Azurite.
        var response = await _factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checks = (await ReadAsync(response)).GetProperty("checks");
        Assert.Equal("Healthy", checks.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("Degraded", checks.GetProperty("message-broker").GetProperty("status").GetString());
        Assert.Equal("Degraded", checks.GetProperty("blob-storage").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_FailsWhenTheDatabaseIsUnreachable()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlServer(
                "Server=127.0.0.1,1;Database=Nowhere;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=True"));
        }));

        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        var database = body.GetProperty("checks").GetProperty("database");
        Assert.Equal("Database unreachable.", database.GetProperty("description").GetString());
    }

    [Fact]
    public async Task UnknownRoutes_ReturnProblemDetails()
    {
        var response = await _factory.CreateClient().GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadAsync(response);
        Assert.Equal(404, body.GetProperty("status").GetInt32());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
