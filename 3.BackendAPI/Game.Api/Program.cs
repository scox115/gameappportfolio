using Game.Api.Health;
using Game.Api.Observability;
using Game.Api.Endpoints; // Add this using statement at the top!
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Azure.Identity;
using Azure.Storage.Blobs;
using Game.Core.Interfaces;
using Game.Infrastructure.Storage;
using Game.Infrastructure.History;
using RabbitMQ.Client;
using Game.Api.Workers; // Add this using statement to register background workers
using Game.Api.Options;
using Game.Api.Auth;
using Game.Api.Battles;
using Game.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Game.Api.Messaging;
using Game.Core.Battles;
using System.Text.Json.Serialization;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.AddObservability();
builder.Services.AddGameHealthChecks();

// Errors come back as RFC 7807 problem details (with a traceId to find them in the logs and traces).
builder.Services.AddProblemDetails();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Adds an "Authorize" button to Swagger UI that sends the JWT as a bearer token.
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Paste the accessToken returned by /api/auth/login."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

// --- 🛡️ REGISTER CORS SECURITY POLICY ---
// Allowed origins come from the "Cors:AllowedOrigins" setting for each environment.
var corsSettings = builder.Configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorFrontendPolicy", policy =>
    {
        policy.WithOrigins(corsSettings.AllowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials() // Essential if you handle secure cookies later
              .WithExposedHeaders(SessionClaims.EndedHeader); // lets the browser read why it was signed out
    });
});

// --- 📨 FREE OPEN-SOURCE MESSAGING SETUP ---
// Broker settings are bound from the "RabbitMq" section and validated at startup.
builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var rabbit = sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
    return new ConnectionFactory
    {
        HostName = rabbit.HostName,
        Port = rabbit.Port,
        VirtualHost = rabbit.VirtualHost,
        UserName = rabbit.UserName,
        Password = rabbit.Password,
        // Give up on an unreachable broker quickly; the sender and consumer retry with backoff.
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
        // Reconnect (and restart the consumer) on its own after a dropped connection.
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true
    };
});

// Read required connection strings up front so a missing setting stops startup immediately.
var sqlConnectionString = GetRequiredConnectionString(builder.Configuration, "DefaultConnection");
var blobConnectionString = GetRequiredConnectionString(builder.Configuration, "AzureBlobStorage");

// Locally this is an Azurite connection string. In Azure it's just the account's blob endpoint
// (https://<account>.blob.core.windows.net) and the API signs in with its managed identity, so no
// storage key exists anywhere in configuration.
builder.Services.AddSingleton(sp => Uri.TryCreate(blobConnectionString, UriKind.Absolute, out var blobEndpoint)
    ? new BlobServiceClient(blobEndpoint, new DefaultAzureCredential())
    : new BlobServiceClient(blobConnectionString));

// Bind your Clean Architecture application interfaces to your infrastructure
builder.Services.AddScoped<IStorageService, AzureBlobStorageService>();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        sqlConnectionString,
        sqlOptions => sqlOptions
            .MigrationsAssembly("Game.Infrastructure")
            // Azure SQL drops connections now and then, and a serverless database that has paused
            // takes a moment to wake up, so retry transient failures instead of failing the request.
            .EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)
    );

    //options.UseInMemoryDatabase("GamePortfolioDb");
});

// --- 🔐 IDENTITY + JWT BEARER AUTHENTICATION ---
// ASP.NET Core Identity stores accounts and hashes passwords; the API issues short-lived JWTs.
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        bearer.MapInboundClaims = false; // keep "sub" as-is instead of the legacy SOAP claim names
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = TokenService.CreateSigningKey(jwt.SigningKey),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // Browsers can't set headers on WebSocket requests, so SignalR sends the token in the query string.
        bearer.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },

            // A valid signature isn't enough: the token must also belong to the account's latest sign-in.
            OnTokenValidated = async context =>
            {
                var sessions = context.HttpContext.RequestServices.GetRequiredService<ActiveSessionValidator>();
                if (!await sessions.IsCurrentAsync(context.Principal!, context.HttpContext.RequestAborted))
                {
                    context.HttpContext.Items[SessionClaims.EndedHeader] = true;
                    context.Fail("The session was replaced by a newer sign-in.");
                }
            },

            OnChallenge = context =>
            {
                if (context.HttpContext.Items.ContainsKey(SessionClaims.EndedHeader))
                {
                    context.Response.Headers[SessionClaims.EndedHeader] = SessionClaims.SignedInElsewhere;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton(TimeProvider.System);

// --- 🛡️ ANTI-CHEAT ---
// Same-network duels pay nothing, and sign-up and sign-in are rate-limited per IP address.
builder.Services.AddOptions<AntiCheatOptions>()
    .Bind(builder.Configuration.GetSection(AntiCheatOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Behind a reverse proxy (such as Azure Container Apps' ingress) the client's address arrives in
// X-Forwarded-For. Only loopback proxies are trusted by default; add the hosting proxy's
// network when deploying (or set ForwardedHeaders:TrustAllProxies, below) so the header can't be spoofed.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Container Apps' ingress is the only way into the API there, but its address isn't fixed, so the
    // Azure deployment sets ForwardedHeaders:TrustAllProxies. Never set it where clients can reach
    // the API directly, or they could fake their IP address.
    if (builder.Configuration.GetValue<bool>("ForwardedHeaders:TrustAllProxies"))
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimits.Registration, context => PerAddress(context, limits => limits.RegistrationsPerHour, TimeSpan.FromHours(1)));
    options.AddPolicy(RateLimits.SignIn, context => PerAddress(context, limits => limits.SignInsPerMinute, TimeSpan.FromMinutes(1)));
    options.AddPolicy(RateLimits.AvatarUpload, context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<IOptions<AntiCheatOptions>>().Value.AvatarUploadsPerHour,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        }));
    options.OnRejected = async (context, cancellationToken) =>
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many attempts from your network. Please wait a little and try again." }, cancellationToken);

    static RateLimitPartition<string> PerAddress(HttpContext context, Func<AntiCheatOptions, int> limit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit(context.RequestServices.GetRequiredService<IOptions<AntiCheatOptions>>().Value),
                Window = window,
                QueueLimit = 0
            });
});
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<ActiveSessionValidator>();
builder.Services.AddScoped<SessionNotifier>();

// --- ⚔️ SERVER-AUTHORITATIVE BATTLES ---
builder.Services.AddSingleton<IBattleRandom, SystemBattleRandom>();
builder.Services.AddSingleton<MatchTelemetryPublisher>();
builder.Services.AddSingleton<TelemetryBrokerStatus>();

// --- 🆚 REAL-TIME PVP ARENA (SignalR) ---
builder.Services.AddSignalR(options => options.AddFilter<ActiveSessionHubFilter>())
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddSingleton<PvpMatchmaker>();
builder.Services.AddScoped<PvpBattleService>();
builder.Services.AddScoped<MatchHistoryProjector>();
builder.Services.AddHostedService<PvpTurnTimeoutWorker>();

// Enums such as battle cards and status travel as readable strings ("DragonClaw", "Won").
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// --- ⚙️ REGISTER NATIVE WORKER RUNTIME LOOPS ---
builder.Services.AddHostedService<MatchTelemetrySender>();
builder.Services.AddHostedService<MatchConsumerWorker>();

var app = builder.Build();

// Unhandled exceptions become a 500 problem-details response instead of an empty body or stack trace,
// and bare error status codes (404, 405...) get a problem-details body too.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();

// --- 🚀 ACTIVATE CORS MIDDLEWARE ---
// This must be placed precisely before MapPlayerEndpoints or UseAuthorization
app.UseCors("BlazorFrontendPolicy");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// --- MAP MINIMAL ENDPOINTS HERE ---
app.MapAuthEndpoints();
app.MapPlayerEndpoints();
app.MapBattleEndpoints();
app.MapPvpEndpoints();
app.MapShopEndpoints();
app.MapClassEndpoints();
app.MapBountyEndpoints();
app.MapHistoryEndpoints();
app.MapGameHealthChecks();
app.MapHub<ArenaHub>(ArenaHub.Path);
app.MapHub<SessionHub>(SessionHub.Path);

app.MapControllers();

// --- AUTOMATIC RUNTIME DATABASE INITIALIZATION ---
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        
        // This checks if the database exists; if not, it automatically runs 
        // all pending migrations and builds your tables instantly inside Docker
        // Migrations only apply to a relational provider (tests swap in the in-memory one).
        if (dbContext.Database.IsRelational())
        {
            await dbContext.Database.MigrateAsync();
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while seeding or migrating the SQL database.");
    }
}

app.Run();

// Fail fast with a clear message instead of a null connection string deep inside a client library.
static string GetRequiredConnectionString(IConfiguration configuration, string name) =>
    configuration.GetConnectionString(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"Connection string '{name}' is not configured. For local development, see docs/local-development.md.");

// Lets the integration tests reference the entry point with WebApplicationFactory<Program>.
public partial class Program;
