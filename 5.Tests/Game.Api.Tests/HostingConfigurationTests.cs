using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Api.Tests;

// Settings the Azure deployment (infra/main.bicep) relies on.
public class HostingConfigurationTests : IClassFixture<GameApiFactory>
{
    private readonly GameApiFactory _factory;

    public HostingConfigurationTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public void ByDefault_OnlyLoopbackProxiesAreTrusted()
    {
        var options = _factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.NotEmpty(options.KnownIPNetworks);
    }

    [Fact]
    public void BehindContainerAppsIngress_AnyProxyIsTrusted()
    {
        using var hosted = _factory.WithWebHostBuilder(builder => builder.UseSetting("ForwardedHeaders:TrustAllProxies", "true"));

        var options = hosted.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Empty(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void ABlobEndpointUrl_IsUsedWithoutAKey()
    {
        using var hosted = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:AzureBlobStorage", "https://stcardarena.blob.core.windows.net/"));

        var blobs = hosted.Services.GetRequiredService<BlobServiceClient>();

        Assert.Equal("stcardarena", blobs.AccountName);
        Assert.False(blobs.CanGenerateAccountSasUri); // no shared key: it signs in with a token credential
    }

    [Theory]
    [InlineData("https://swa-cardarena.azurestaticapps.net")]
    [InlineData("https://play.example.com")]
    public async Task WithACustomDomain_BothClientAddressesMayCallTheApi(string origin)
    {
        using var hosted = _factory.WithWebHostBuilder(builder => builder
            .UseSetting("Cors:AllowedOrigins:0", "https://swa-cardarena.azurestaticapps.net")
            .UseSetting("Cors:AllowedOrigins:1", "https://play.example.com"));
        using var client = hosted.CreateClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/players/me");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var response = await client.SendAsync(preflight);

        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task AnUnlistedOrigin_IsNotAllowed()
    {
        using var hosted = _factory.WithWebHostBuilder(builder => builder
            .UseSetting("Cors:AllowedOrigins:0", "https://swa-cardarena.azurestaticapps.net"));
        using var client = hosted.CreateClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/players/me");
        preflight.Headers.Add("Origin", "https://evil.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var response = await client.SendAsync(preflight);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
