namespace Game.Api.Security;

// The API only ever returns JSON, so its responses tell the browser to treat them as nothing more:
// no sniffing them into HTML or script, no loading anything from them, no framing, no referrer.
// The game client's own Content Security Policy lives in the client's staticwebapp.config.json.
public static class SecurityHeaders
{
    /// <summary>Nothing may load from an API response, and no page may frame it.</summary>
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    public static IServiceCollection AddGameSecurityHeaders(this IServiceCollection services)
    {
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            // Left off: the API lives on a host Azure owns, and preload is a one-way door.
            options.IncludeSubDomains = false;
            options.Preload = false;
        });
        return services;
    }

    /// <summary>Adds the headers to every response, errors included, so call it first.</summary>
    public static IApplicationBuilder UseGameSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";

                // Swagger UI (Development only) is a real page with scripts and styles.
                if (!context.Request.Path.StartsWithSegments("/swagger"))
                {
                    headers.ContentSecurityPolicy = ApiContentSecurityPolicy;
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
}
