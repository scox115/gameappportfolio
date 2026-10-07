using Game.Api.Operations;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Game.Api.Endpoints;
using Game.Api.Features;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Game.Api.Versioning;

// The game API is versioned in the URL (/api/v1/players/me), so a breaking change ships as v2
// alongside v1 instead of breaking clients that haven't updated yet. Health checks and the SignalR
// hubs stay unversioned: probes and the hub protocol have their own contracts.
public static class ApiVersioning
{
    public static readonly ApiVersion V1 = new(1, 0);

    /// <summary>Prefix of the routes from before versioning, still served as v1 for old clients.</summary>
    public const string LegacyPrefix = "/api";

    /// <summary>When the unversioned routes were deprecated (RFC 9745 Deprecation header).</summary>
    public static readonly DateTimeOffset LegacyDeprecatedOn = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>When the unversioned routes may be removed (RFC 8594 Sunset header).</summary>
    public static readonly DateTimeOffset LegacySunsetOn = new(2027, 4, 1, 0, 0, 0, TimeSpan.Zero);

    public static IServiceCollection AddGameApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = V1;
                // Only the legacy routes have no version in the URL; they mean v1.
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
                // Every response says which versions exist (api-supported-versions, api-deprecated-versions).
                options.ReportApiVersions = true;
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";          // Swagger documents named v1, v2...
                options.SubstituteApiVersionInUrl = true;      // show /api/v1/... rather than /api/v{version}/...
            });

        services.AddTransient<IConfigureOptions<SwaggerGenOptions>, SwaggerDocumentPerVersion>();
        return services;
    }

    /// <summary>Maps every game endpoint under /api/v1, and again under the legacy /api prefix.</summary>
    public static void MapGameApi(this WebApplication app)
    {
        var api = app.NewVersionedApi("Game");

        api.MapGroup("/api/v{version:apiVersion}")
           .HasApiVersion(V1)
           .MapGameEndpoints();

        // Kept so clients built before versioning keep working until the sunset date.
        // Hidden from Swagger, and every response points to its v1 replacement.
        api.MapGroup(LegacyPrefix)
           .HasApiVersion(V1)
           .MapGameEndpoints()
           .ExcludeFromDescription()
           .AddEndpointFilter(async (context, next) =>
           {
               var http = context.HttpContext;
               var successor = "/api/v1" + http.Request.Path.Value![LegacyPrefix.Length..];
               http.Response.Headers["Deprecation"] = $"@{LegacyDeprecatedOn.ToUnixTimeSeconds()}";
               http.Response.Headers["Sunset"] = LegacySunsetOn.ToString("R");
               http.Response.Headers.Link = $"<{successor}>; rel=\"successor-version\"";
               return await next(context);
           });
    }

    public static void UseGameApiSwaggerUI(this WebApplication app)
    {
        app.UseSwaggerUI(options =>
        {
            // Newest version first, so it opens by default.
            foreach (var description in app.DescribeApiVersions().Reverse())
            {
                options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json", description.GroupName.ToUpperInvariant());
            }
        });
    }

    private static RouteGroupBuilder MapGameEndpoints(this RouteGroupBuilder group)
    {
        group.MapAuthEndpoints();
        group.MapPlayerEndpoints();
        group.MapAccountEndpoints();
        group.MapAdminEndpoints();
        group.MapBattleEndpoints();
        group.MapPvpEndpoints();
        group.MapShopEndpoints();
        group.MapClassEndpoints();
        group.MapBountyEndpoints();
        group.MapHistoryEndpoints();
        group.MapFeatureEndpoints();
        group.MapStatusEndpoints();
        return group;
    }

    // One Swagger document per API version, so adding v2 needs no Swagger changes.
    private sealed class SwaggerDocumentPerVersion(IApiVersionDescriptionProvider versions) : IConfigureOptions<SwaggerGenOptions>
    {
        public void Configure(SwaggerGenOptions options)
        {
            foreach (var description in versions.ApiVersionDescriptions)
            {
                options.SwaggerDoc(description.GroupName, new OpenApiInfo
                {
                    Title = "Kings of the Card Arena API",
                    Version = description.ApiVersion.ToString(),
                    Description = description.IsDeprecated
                        ? "This version is deprecated. Move to the newest version before its sunset date."
                        : "Routes start with /api/v{version}. The unversioned /api routes are deprecated aliases of v1."
                });
            }
        }
    }
}
