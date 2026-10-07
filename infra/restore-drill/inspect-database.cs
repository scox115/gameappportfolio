// Prints what a database holds as JSON: its EF Core migrations and the row count of every table.
// The restore drill runs it against the live database and the restored copy and compares the two.
//
//   dotnet run inspect-database.cs -- <server> <database>
//
// It signs in with the Entra ID access token in SQL_ACCESS_TOKEN (the workflow gets one from the Azure CLI).
// For a local SQL Server, set SQL_CONNECTION_STRING instead and pass the database name only.
#:package Microsoft.Data.SqlClient@6.1.6
#:property PublishAot=false

using System.Text.Json;
using Microsoft.Data.SqlClient;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: dotnet run inspect-database.cs -- [server] <database>");
    return 2;
}

var database = args[^1];
var localConnectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
await using var connection = string.IsNullOrEmpty(localConnectionString)
    ? new SqlConnection(new SqlConnectionStringBuilder
    {
        DataSource = $"tcp:{args[0]},1433",
        InitialCatalog = database,
        Encrypt = true,
        // A restored database starts paused-cold, and so may the live one (it auto-pauses).
        ConnectTimeout = 120,
    }.ConnectionString)
    {
        AccessToken = Environment.GetEnvironmentVariable("SQL_ACCESS_TOKEN")
            ?? throw new InvalidOperationException("Set SQL_ACCESS_TOKEN or SQL_CONNECTION_STRING."),
    }
    : new SqlConnection(new SqlConnectionStringBuilder(localConnectionString) { InitialCatalog = database }.ConnectionString);

await connection.OpenAsync();

var migrations = new List<string>();
await using (var command = new SqlCommand(
    "IF OBJECT_ID(N'__EFMigrationsHistory') IS NOT NULL SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId", connection))
await using (var reader = await command.ExecuteReaderAsync())
{
    while (await reader.ReadAsync()) migrations.Add(reader.GetString(0));
}

// Row counts from the catalog: instant, even for big tables, and exact enough for a comparison.
var tables = new SortedDictionary<string, long>(StringComparer.Ordinal);
await using (var command = new SqlCommand("""
    SELECT s.name + '.' + t.name, SUM(p.rows)
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
    WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
    GROUP BY s.name, t.name
    """, connection))
await using (var reader = await command.ExecuteReaderAsync())
{
    while (await reader.ReadAsync()) tables[reader.GetString(0)] = reader.GetInt64(1);
}

Console.WriteLine(JsonSerializer.Serialize(new { migrations, tables }));
return 0;
