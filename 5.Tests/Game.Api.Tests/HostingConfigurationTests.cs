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
}
