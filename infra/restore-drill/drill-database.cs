// The restore drill's database work, as a .NET 10 single-file app (no project needed):
//
//   dotnet run drill-database.cs -- inspect <server> <database>
//       Prints the database's EF Core migrations and the row count of every table as JSON. The drill
//       runs it on the live database and on the restored copy and compares the two.
//
//   dotnet run drill-database.cs -- record <server> <database> <passed|failed> <detail>
//       Adds the drill's result to the live database's OperationsEvents table, which the public status
//       page shows.
//
// It signs in with the Entra ID access token in SQL_ACCESS_TOKEN (the workflow gets one from the Azure CLI).
// For a local SQL Server, set SQL_CONNECTION_STRING instead; <server> is then ignored.
#:package Microsoft.Data.SqlClient@6.1.6
#:property PublishAot=false

using System.Text.Json;
using Microsoft.Data.SqlClient;

if (args is not (["inspect", _, _] or ["record", _, _, "passed" or "failed", _]))
{
    Console.Error.WriteLine("Usage: drill-database.cs inspect <server> <database>");
    Console.Error.WriteLine("       drill-database.cs record <server> <database> <passed|failed> <detail>");
    return 2;
}

await using var connection = Connect(server: args[1], database: args[2]);
await connection.OpenAsync();

if (args[0] == "record")
{
    // Kind is stored by name (see OperationsEventConfiguration), and the detail fits the column.
    await using var insert = new SqlCommand(
        "INSERT INTO OperationsEvents (Kind, OccurredAt, Succeeded, Detail) VALUES ('RestoreDrill', SYSDATETIMEOFFSET(), @succeeded, @detail)",
        connection);
    insert.Parameters.AddWithValue("@succeeded", args[3] == "passed");
    insert.Parameters.AddWithValue("@detail", args[4].Length <= 500 ? args[4] : args[4][..500]);
    await insert.ExecuteNonQueryAsync();
    return 0;
}

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

static SqlConnection Connect(string server, string database)
{
    var local = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
    if (!string.IsNullOrEmpty(local))
    {
        return new SqlConnection(new SqlConnectionStringBuilder(local) { InitialCatalog = database }.ConnectionString);
    }

    return new SqlConnection(new SqlConnectionStringBuilder
    {
        DataSource = $"tcp:{server},1433",
        InitialCatalog = database,
        Encrypt = true,
        // A restored database starts cold, and so may the live one (it auto-pauses).
        ConnectTimeout = 120,
    }.ConnectionString)
    {
        AccessToken = Environment.GetEnvironmentVariable("SQL_ACCESS_TOKEN")
            ?? throw new InvalidOperationException("Set SQL_ACCESS_TOKEN or SQL_CONNECTION_STRING."),
    };
}
