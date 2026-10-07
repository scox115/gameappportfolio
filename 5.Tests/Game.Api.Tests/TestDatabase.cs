using Game.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Tests;

/// <summary>
/// A throwaway relational database for tests that depend on real transactions and concurrency checks,
/// which the in-memory provider doesn't have. SQLite always runs. SQL Server, the engine the game runs on
/// in Azure, runs too when TEST_SQLSERVER holds a connection string to a server (CI starts one; locally,
/// see docs/local-development.md). Each database is new, built by the real migrations, and dropped afterwards.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    public const string Sqlite = "SQLite";
    public const string SqlServer = "SQL Server";

    /// <summary>The SQL Server to test against, or null when none is configured.</summary>
    public static string? SqlServerConnection => Environment.GetEnvironmentVariable("TEST_SQLSERVER") is { Length: > 0 } value ? value : null;

    /// <summary>The engines to run a theory on: SQLite, and SQL Server when one is configured.</summary>
    public static TheoryData<string> Engines()
    {
        var engines = new TheoryData<string> { Sqlite };
        if (SqlServerConnection is not null) engines.Add(SqlServer);
        return engines;
    }

    private readonly string? _sqliteFile;
    private readonly string? _sqlServerName;

    public string Engine { get; }
    public string ConnectionString { get; }

    private TestDatabase(string engine)
    {
        Engine = engine;
        if (engine == SqlServer)
        {
            var server = SqlServerConnection ?? throw new InvalidOperationException("Set TEST_SQLSERVER to test against SQL Server.");
            _sqlServerName = $"CardArenaTests_{Guid.NewGuid():N}";
            ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = _sqlServerName }.ConnectionString;
        }
        else
        {
            _sqliteFile = Path.Combine(Path.GetTempPath(), $"card-arena-{Guid.NewGuid():N}.db");
            // Writers wait their turn for up to 30 seconds rather than failing straight away.
            ConnectionString = $"Data Source={_sqliteFile};Default Timeout=30;Pooling=False";
        }
    }

    /// <summary>Creates an empty database with the game's schema.</summary>
    public static TestDatabase Create(string engine)
    {
        var database = new TestDatabase(engine);
        using var db = database.NewContext();
        if (engine == SqlServer)
        {
            db.Database.Migrate(); // the real migrations, as Azure runs them
        }
        else
        {
            db.Database.EnsureCreated(); // the migrations are written for SQL Server
        }
        return database;
    }

    /// <summary>A database for an API host to create itself at startup, with its migrations.</summary>
    public static TestDatabase ForApi() => new(SqlServer);

    public void Configure(DbContextOptionsBuilder options)
    {
        if (Engine == SqlServer)
        {
            options.UseSqlServer(ConnectionString, sql => sql.MigrationsAssembly("Game.Infrastructure"));
        }
        else
        {
            options.UseSqlite(ConnectionString);
        }
    }

    public AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        Configure(options);
        return new AppDbContext(options.Options);
    }

    public void Dispose()
    {
        if (_sqliteFile is not null)
        {
            File.Delete(_sqliteFile);
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        master.Open();
        using var drop = master.CreateCommand();
        drop.CommandText = $"""
            IF DB_ID('{_sqlServerName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_sqlServerName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_sqlServerName}];
            END
            """;
        drop.ExecuteNonQuery();
    }
}

/// <summary>A test that needs SQL Server; skipped unless TEST_SQLSERVER is set.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (TestDatabase.SqlServerConnection is null) Skip = "Set TEST_SQLSERVER to run the tests against SQL Server.";
    }
}
