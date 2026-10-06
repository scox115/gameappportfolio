// Starts the whole game for local development with one command: SQL Server, Azurite and RabbitMQ
// in Docker, then the API and the Blazor client, plus the Aspire dashboard for logs, traces and
// metrics. Run it with:  dotnet run --project 6.Aspire/Game.AppHost
var builder = DistributedApplication.CreateBuilder(args);

// Passwords and the token signing key are generated on the first run and kept in this
// project's user-secrets, so the database and logins survive restarts.
var jwtSigningKey = builder.AddParameter("jwt-signing-key",
    new GenerateParameterDefault { MinLength = 48, Special = false }, secret: true, persist: true);

var sql = builder.AddSqlServer("sql")
    .WithDataVolume("card-arena-sql")
    .WithLifetime(ContainerLifetime.Persistent);
var gameDb = sql.AddDatabase("DefaultConnection", databaseName: "GameDb");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator
        .WithDataVolume("card-arena-azurite")
        .WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs("AzureBlobStorage");

var rabbitPassword = builder.AddParameter("rabbitmq-password",
    new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);
var rabbit = builder.AddRabbitMQ("rabbitmq", password: rabbitPassword)
    .WithManagementPlugin()
    .WithDataVolume("card-arena-rabbitmq")
    .WithLifetime(ContainerLifetime.Persistent);

// The API keeps its own settings names (ConnectionStrings:DefaultConnection, ConnectionStrings:AzureBlobStorage,
// RabbitMq:*, Jwt:SigningKey), so the same code runs here, in Docker Compose and in Azure.
var api = builder.AddProject<Projects.Game_Api>("api", launchProfileName: "http")
    .WithReference(gameDb).WaitFor(gameDb)
    .WithReference(blobs).WaitFor(blobs)
    .WithEnvironment("RabbitMq__HostName", rabbit.GetEndpoint("tcp").Property(EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.GetEndpoint("tcp").Property(EndpointProperty.Port))
    .WithEnvironment("RabbitMq__UserName", "guest")
    .WithEnvironment("RabbitMq__Password", rabbitPassword)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WaitFor(rabbit)
    .WithHttpHealthCheck("/health/ready");

// The client is static files in the browser, so it can't read environment variables: it finds the
// API through wwwroot/appsettings.Development.json (http://localhost:5005, the API's launch profile port).
builder.AddProject<Projects.Game_Client>("client", launchProfileName: "http")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
