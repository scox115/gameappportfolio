using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Game.Api.Tests;

public class RateLimitTests : IClassFixture<GameApiFactory>
{
    private readonly GameApiFactory _factory;

    public RateLimitTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task TooManyNewAccountsFromOneAddress_AreTurnedAway()
    {
        using var strict = _factory.WithWebHostBuilder(builder => builder.UseSetting("AntiCheat:RegistrationsPerHour", "2"));
        var client = strict.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { Username = $"rate{Guid.NewGuid():N}"[..20], Password = "Arena-Pass1" });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task TooManySignInAttemptsFromOneAddress_AreTurnedAway()
    {
        using var strict = _factory.WithWebHostBuilder(builder => builder.UseSetting("AntiCheat:SignInsPerMinute", "2"));
        var client = strict.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { Username = "nobody-here", Password = "Wrong-Pass1" });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses.Last());
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
    }
}
