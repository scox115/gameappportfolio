using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Game.AppHost.Tests;

// Checks the Aspire app model without starting anything, so it needs no Docker.
public class AppHostTests(AppHostFixture fixture) : IClassFixture<AppHostFixture>
{
    private readonly DistributedApplication app = fixture.App;

    [Fact]
    public void TheGameAndItsServices_AreAllDefined()
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var names = model.Resources.Select(r => r.Name).ToHashSet();

        Assert.Superset(new HashSet<string> { "sql", "DefaultConnection", "storage", "AzureBlobStorage", "rabbitmq", "api", "client" }, names);
    }

    [Fact]
    public async Task TheApi_GetsItsSettingsUnderTheNamesItAlreadyReads()
    {
        var api = Resource<ProjectResource>(app, "api");

        // The simplest way to resolve the values for a test; its replacement needs a full execution context.
#pragma warning disable CS0618
        var environment = await api.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Run);
#pragma warning restore CS0618

        foreach (var key in new[]
                 {
                     "ConnectionStrings__DefaultConnection", "ConnectionStrings__AzureBlobStorage",
                     "RabbitMq__HostName", "RabbitMq__Port", "RabbitMq__UserName", "RabbitMq__Password",
                     "Jwt__SigningKey",
                 })
        {
            Assert.True(environment.ContainsKey(key), $"The API is missing {key}.");
        }
        Assert.Contains("GameDb", environment["ConnectionStrings__DefaultConnection"]);
    }

    [Fact]
    public void TheApi_KeepsPort5005_BecauseTheClientsSettingsPointThere()
    {
        var api = Resource<ProjectResource>(app, "api");

        var http = Assert.Single(api.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");

        Assert.Equal(5005, http.Port);
    }

    [Fact]
    public void TheApi_WaitsForTheDatabaseAndBroker_AndReportsReadiness()
    {
        var api = Resource<ProjectResource>(app, "api");

        var waitsFor = api.Annotations.OfType<WaitAnnotation>().Select(w => w.Resource.Name).ToList();

        Assert.Contains("DefaultConnection", waitsFor);
        Assert.Contains("rabbitmq", waitsFor);
        Assert.NotEmpty(api.Annotations.OfType<HealthCheckAnnotation>());
    }

    private static T Resource<T>(DistributedApplication app, string name) where T : IResource =>
        app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<T>().Single(r => r.Name == name);
}

// Builds the app model once for all the tests.
public sealed class AppHostFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Game_AppHost>();
        App = await builder.BuildAsync();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();
}
