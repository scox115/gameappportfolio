using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Api.Models;
using Microsoft.AspNetCore.Hosting;

namespace Game.Api.Tests;

public class AvatarUploadTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46];

    private readonly GameApiFactory _factory;

    public AvatarUploadTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Png_IsStoredAsPngAndBecomesThePortrait()
    {
        var (client, playerId) = await SignedInPlayerAsync(_factory);

        var response = await UploadAsync(client, PngBytes, "me.png", "image/png");

        response.EnsureSuccessStatusCode();
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("avatarUrl").GetString()!;
        var stored = _factory.Storage.Uploaded.Single(f => f.Url == url);
        Assert.Equal("image/png", stored.ContentType);
        Assert.EndsWith("_avatar.png", url);
        Assert.Equal(PngBytes, stored.Content);

        var profile = await client.GetFromJsonAsync<JsonElement>($"/api/v1/players/{playerId}", Json);
        Assert.Equal(url, profile.GetProperty("avatarUrl").GetString());
    }

    [Fact]
    public async Task Jpeg_IsStoredAsJpegWhateverTheBrowserClaims()
    {
        var (client, _) = await SignedInPlayerAsync(_factory);

        var response = await UploadAsync(client, JpegBytes, "holiday.png", "image/png");

        response.EnsureSuccessStatusCode();
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("avatarUrl").GetString()!;
        Assert.Equal("image/jpeg", _factory.Storage.Uploaded.Single(f => f.Url == url).ContentType);
        Assert.EndsWith("_avatar.jpg", url);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "evil.png", "image/png")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", "pic.svg", "image/svg+xml")]
    [InlineData("GIF89a", "anim.gif", "image/gif")]
    public async Task FilesThatArentPngOrJpeg_AreRejected(string content, string fileName, string contentType)
    {
        var (client, _) = await SignedInPlayerAsync(_factory);
        var uploadsBefore = _factory.Storage.Uploaded.Count;

        var response = await UploadAsync(client, System.Text.Encoding.UTF8.GetBytes(content), fileName, contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PNG or JPG", await response.Content.ReadAsStringAsync());
        Assert.Equal(uploadsBefore, _factory.Storage.Uploaded.Count);
    }

    [Fact]
    public async Task FilesOver5Mb_AreRejected()
    {
        var (client, _) = await SignedInPlayerAsync(_factory);
        var tooBig = new byte[5 * 1024 * 1024 + 1];
        PngBytes.CopyTo(tooBig, 0);

        var response = await UploadAsync(client, tooBig, "huge.png", "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("5 MB", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NewPortrait_DeletesTheOldOne()
    {
        var (client, _) = await SignedInPlayerAsync(_factory);

        var first = await UploadAsync(client, PngBytes, "a.png", "image/png");
        var firstUrl = (await first.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("avatarUrl").GetString()!;
        Assert.DoesNotContain(firstUrl, _factory.Storage.Deleted);

        (await UploadAsync(client, JpegBytes, "b.jpg", "image/jpeg")).EnsureSuccessStatusCode();

        Assert.Contains(firstUrl, _factory.Storage.Deleted);
    }

    [Fact]
    public async Task TooManyUploadsByOnePlayer_AreTurnedAway()
    {
        using var strict = _factory.WithWebHostBuilder(builder => builder.UseSetting("AntiCheat:AvatarUploadsPerHour", "2"));
        var (client, _) = await SignedInPlayerAsync(strict);
        var (otherClient, _) = await SignedInPlayerAsync(strict);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await UploadAsync(client, PngBytes, "me.png", "image/png")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        // The limit is per player, so someone else on the same network can still upload.
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(otherClient, PngBytes, "me.png", "image/png")).StatusCode);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string fileName, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "File", fileName);
        return await client.PostAsync("/api/v1/players/me/avatar", form);
    }

    private static async Task<(HttpClient Client, Guid PlayerId)> SignedInPlayerAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var username = $"pic{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth.Player.Id);
    }
}
