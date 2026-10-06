using System.Net;
using Game.Api.Security;

namespace Game.Api.Tests;

public class SecurityHeadersTests
{
    [Theory]
    [InlineData("/api/v1/classes")]          // a normal response
    [InlineData("/api/v1/players/me")]       // 401
    [InlineData("/api/v1/no-such-route")]    // 404 problem+json
    [InlineData("/health/live")]
    public async Task EveryResponse_CarriesTheSecurityHeaders(string path)
    {
        using var factory = new GameApiFactory();

        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal(SecurityHeaders.ApiContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"));
    }

    [Fact]
    public async Task OverHttps_BrowsersAreToldToStayOnHttpsForAYear()
    {
        using var factory = new GameApiFactory();
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://arena.example") });

        var response = await client.GetAsync("/health/live");

        Assert.Equal("max-age=31536000", Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task OverPlainHttp_NoHstsHeaderIsSent()
    {
        // Browsers ignore HSTS over HTTP, and sending it on localhost would pin developers to HTTPS.
        using var factory = new GameApiFactory();

        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : throw new Xunit.Sdk.XunitException($"Missing header {name} on {(int)response.StatusCode}.");
}
