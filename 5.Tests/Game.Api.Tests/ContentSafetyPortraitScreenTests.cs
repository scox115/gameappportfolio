using System.Net;
using System.Text;
using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Core.Pipeline;
using Game.Core.Moderation;
using Game.Infrastructure.Moderation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Api.Tests;

// The real Content Safety client, answered by a stand-in for the service, so the request it sends and the
// way each kind of answer is read are both checked without an Azure resource.
public class ContentSafetyPortraitScreenTests
{
    private static readonly byte[] Image = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Fact]
    public async Task TheImageIsSent_AndEachHarmIsJudged()
    {
        var service = new FakeService(HttpStatusCode.OK, """
            {"categoriesAnalysis":[{"category":"Hate","severity":0},{"category":"SelfHarm","severity":0},
             {"category":"Sexual","severity":4},{"category":"Violence","severity":2}]}
            """);

        var screening = await Screen(service).ScreenAsync(Image);

        Assert.Equal(new PortraitScreening(PortraitVerdict.Blocked, PortraitHarm.Sexual), screening);
        Assert.EndsWith("/contentsafety/image:analyze", service.Request!.RequestUri!.AbsolutePath);
        Assert.Contains(Convert.ToBase64String(Image), service.Body);
        Assert.Equal("Bearer", service.Request.Headers.Authorization?.Scheme); // the managed identity, not a key
    }

    [Fact]
    public async Task ASafeImage_IsAllowed()
    {
        var service = new FakeService(HttpStatusCode.OK, """
            {"categoriesAnalysis":[{"category":"Hate","severity":0},{"category":"Violence","severity":2}]}
            """);

        Assert.Equal(PortraitScreening.Allowed, await Screen(service).ScreenAsync(Image));
    }

    [Fact]
    public async Task AnImageTheServiceCantRead_IsUnreadable()
    {
        var service = new FakeService(HttpStatusCode.BadRequest,
            """{"error":{"code":"InvalidRequestBody","message":"The image size is too small."}}""");

        Assert.Equal(PortraitScreening.Unreadable, await Screen(service).ScreenAsync(Image));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)] // the free tier's monthly allowance is used up
    [InlineData(HttpStatusCode.Forbidden)]       // the API's identity isn't allowed in yet
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task WhenTheServiceFails_ThePortraitIsUnchecked(HttpStatusCode status)
    {
        var service = new FakeService(status, """{"error":{"code":"Failed","message":"No."}}""");

        Assert.Equal(PortraitScreening.Unavailable, await Screen(service).ScreenAsync(Image));
    }

    [Fact]
    public async Task WhenTheServiceCantBeReached_ThePortraitIsUnchecked()
    {
        var service = new FakeService(failWith: new HttpRequestException("Connection refused"));

        Assert.Equal(PortraitScreening.Unavailable, await Screen(service).ScreenAsync(Image));
    }

    private static ContentSafetyPortraitScreen Screen(FakeService service)
    {
        var options = new ContentSafetyClientOptions { Transport = new HttpClientTransport(service) };
        options.Retry.MaxRetries = 0;
        var client = new ContentSafetyClient(new Uri("https://cs-test.cognitiveservices.azure.com/"), new FakeCredential(), options);
        return new ContentSafetyPortraitScreen(client, NullLogger<ContentSafetyPortraitScreen>.Instance);
    }

    private sealed class FakeService(HttpStatusCode status = HttpStatusCode.OK, string body = "{}", Exception? failWith = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (failWith is not null) throw failWith;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
