using Game.Api.Endpoints; // Add this using statement at the top!
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs;
using Game.Core.Interfaces;
using Game.Infrastructure.Storage;
using RabbitMQ.Client;
using Game.Api.Workers; // Add this using statement to register background workers

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- 🛡️ REGISTER CORS SECURITY POLICY ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorFrontendPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5091", "http://localhost:5005", "http://localhost:5123", "https://localhost:7123", "http://localhost:5000", "https://localhost:5001") // Add all your Blazor dev URLs here
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Essential if you handle secure cookies later
    });
});

// --- 📨 FREE OPEN-SOURCE MESSAGING SETUP ---
// This registers a factory singleton that handles connecting to your Docker RabbitMQ container
builder.Services.AddSingleton<IConnectionFactory>(sp => new ConnectionFactory
{
    HostName = "localhost",
    Port = 5672,
    UserName = "guest",
    Password = "guest" // Default Docker credentials
});

// Register Azure SDK client library using connection configurations
builder.Services.AddSingleton(sp => 
    new BlobServiceClient(builder.Configuration.GetConnectionString("AzureBlobStorage")));

// Bind your Clean Architecture application interfaces to your infrastructure
builder.Services.AddScoped<IStorageService, AzureBlobStorageService>();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
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
