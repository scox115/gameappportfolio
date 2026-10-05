using Game.Api.Endpoints; // Add this using statement at the top!
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs;
using Game.Core.Interfaces;
using Game.Infrastructure.Storage;
using RabbitMQ.Client;
using Game.Api.Workers; // Add this using statement to register background workers
using Game.Api.Options;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
              .AllowCredentials(); // Essential if you handle secure cookies later
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
        Password = rabbit.Password
    };
});

// Read required connection strings up front so a missing setting stops startup immediately.
var sqlConnectionString = GetRequiredConnectionString(builder.Configuration, "DefaultConnection");
var blobConnectionString = GetRequiredConnectionString(builder.Configuration, "AzureBlobStorage");

// Register Azure SDK client library using connection configurations
builder.Services.AddSingleton(sp => new BlobServiceClient(blobConnectionString));

// Bind your Clean Architecture application interfaces to your infrastructure
builder.Services.AddScoped<IStorageService, AzureBlobStorageService>();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        sqlConnectionString,
        sqlOptions => sqlOptions.MigrationsAssembly("Game.Infrastructure")
    );

    //options.UseInMemoryDatabase("GamePortfolioDb");
});

// --- ⚙️ REGISTER NATIVE WORKER RUNTIME LOOPS ---
builder.Services.AddHostedService<MatchConsumerWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// --- 🚀 ACTIVATE CORS MIDDLEWARE ---
// This must be placed precisely before MapPlayerEndpoints or UseAuthorization
app.UseCors("BlazorFrontendPolicy");

app.UseAuthorization();

// --- MAP MINIMAL ENDPOINTS HERE ---
app.MapPlayerEndpoints();
app.MapMatchEndpoints();

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
        await dbContext.Database.MigrateAsync();
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
