using Microsoft.Extensions.Options;
using Game.Api.Options;
using Azure.Identity;
using Microsoft.FeatureManagement;

namespace Game.Api.Features;

/// <summary>
/// Feature flags that switch parts of the game off without a deploy, for example to close the
/// Gold Shop while an exploit is fixed. They are all on unless switched off.
///
/// Locally the flags come from the "FeatureManagement" section of appsettings. In Azure they can be
/// flipped in Azure App Configuration (Feature manager), which the API re-reads every couple of minutes.
/// </summary>
public static class GameFeatures
{
    public const string Duels = "Duels";
    public const string HeroicBoss = "HeroicBoss";
    public const string GoldShop = "GoldShop";

    /// <summary>How often the API asks App Configuration for changes. The free tier allows 1,000 requests a day.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private const string AppConfigEndpointKey = "AppConfig:Endpoint";

    public static WebApplicationBuilder AddGameFeatures(this WebApplicationBuilder builder)
    {
        if (builder.Configuration[AppConfigEndpointKey] is { Length: > 0 } endpoint)
        {
            // optional: true, so the API still starts (with the appsettings defaults) if the store is unreachable.
            builder.Configuration.AddAzureAppConfiguration(options => options
                .Connect(new Uri(endpoint), new DefaultAzureCredential())
                .UseFeatureFlags(flags => flags.SetRefreshInterval(RefreshInterval))
                .ConfigureStartupOptions(startup => startup.Timeout = TimeSpan.FromSeconds(15)),
                optional: true);
            builder.Services.AddAzureAppConfiguration();
        }

        builder.Services.AddFeatureManagement();
        return builder;
    }

    /// <summary>Checks App Configuration for changed flags as requests come in (at most once per refresh interval).</summary>
    public static WebApplication UseGameFeatures(this WebApplication app)
    {
        if (app.Configuration[AppConfigEndpointKey] is { Length: > 0 })
        {
            app.UseAzureAppConfiguration();
        }
        return app;
    }

    /// <summary>Answers 503 with a problem body while the feature is switched off.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string feature) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var features = context.HttpContext.RequestServices.GetRequiredService<IFeatureManager>();
            return await features.IsEnabledAsync(feature)
                ? await next(context)
                : SwitchedOff(feature);
        });

    public static IResult SwitchedOff(string feature) => Results.Problem(
        title: $"{DisplayName(feature)} is switched off",
        detail: "This part of the game is switched off for now. Please try again later.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    public static string DisplayName(string feature) => feature switch
    {
        Duels => "Duels",
        HeroicBoss => "The Heroic boss",
        GoldShop => "The Gold Shop",
        _ => feature
    };

    /// <summary>GET /features: which switchable features are on, so the client can hide what's off.</summary>
    public static void MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/features", async (IFeatureManager features, IOptions<EmailOptions> email) => Results.Ok(new FeaturesResponse(
                await features.IsEnabledAsync(Duels),
                await features.IsEnabledAsync(HeroicBoss),
                await features.IsEnabledAsync(GoldShop),
                email.Value.Enabled)))
            .WithTags("Features")
            .AllowAnonymous();
    }
}

/// <param name="AccountRecovery">Whether players can add a recovery email and reset a forgotten password.
/// Not a feature flag: it is on when the deployment has set up email.</param>
public record FeaturesResponse(bool Duels, bool HeroicBoss, bool GoldShop, bool AccountRecovery);
