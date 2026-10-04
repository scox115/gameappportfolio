using Game.Api.Endpoints; // Add this using statement at the top!
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs;
using Game.Core.Interfaces;
using Game.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();

// --- MAP MINIMAL ENDPOINTS HERE ---
app.MapPlayerEndpoints();

app.MapControllers();

app.Run();
