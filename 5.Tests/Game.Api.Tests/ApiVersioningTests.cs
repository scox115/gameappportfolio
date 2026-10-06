using System.Net;
using Game.Api.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace Game.Api.Tests;

public class ApiVersioningTests : IClassFixture<GameApiFactory>
{
    private readonly GameApiFactory _factory;

    public ApiVersioningTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task V1_AnswersAndReportsTheSupportedVersions()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/classes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1.0", string.Join(",", response.Headers.GetValues("api-supported-versions")));
        Assert.False(response.Headers.Contains("Deprecation"));
    }

    [Fact]
    public async Task TheUnversionedRoutes_StillWorkAsV1_ButAreMarkedDeprecated()
    {
        var response = await _factory.CreateClient().GetAsync("/api/classes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"@{ApiVersioning.LegacyDeprecatedOn.ToUnixTimeSeconds()}", response.Headers.GetValues("Deprecation").Single());
        Assert.Equal(ApiVersioning.LegacySunsetOn.ToString("R"), response.Headers.GetValues("Sunset").Single());
        Assert.Equal("</api/v1/classes>; rel=\"successor-version\"", response.Headers.GetValues("Link").Single());
    }

    [Fact]
    public async Task TheUnversionedRoutes_KeepTheirRulesSuchAsSignIn()
    {
        var response = await _factory.CreateClient().GetAsync("/api/players/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownVersion_IsNotFound()
    {
        // In the URL a version is part of the address, so a version that doesn't exist is a 404.
        var response = await _factory.CreateClient().GetAsync("/api/v2/classes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Swagger_DocumentsV1Routes_AndHidesTheDeprecatedAliases()
    {
        var swagger = _factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var paths = swagger.Paths.Keys.ToList();

        Assert.Contains("/api/v1/players/me", paths);
        Assert.Contains("/api/v1/battles/pve/{battleId}/turns", paths);
        Assert.All(paths, path => Assert.StartsWith("/api/v1/", path));
    }
}
