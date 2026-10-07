using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Tests;

public class MigrationTests
{
    // Catches a model change that was committed without its migration. Needs no server: EF compares the model
    // with the migrations' snapshot, for SQL Server as Azure runs it.
    [Fact]
    public void EveryModelChange_HasAMigration()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused", sql => sql.MigrationsAssembly("Game.Infrastructure"))
            .Options);

        Assert.False(db.Database.HasPendingModelChanges(), "The model has changed since the last migration. Run dotnet ef migrations add.");
    }

    // The migrations build a new database from nothing on a real SQL Server, as the first deploy to a new
    // environment does, and leave nothing pending.
    [SqlServerFact]
    public void TheMigrations_BuildAnEmptyDatabase()
    {
        using var database = TestDatabase.Create(TestDatabase.SqlServer);
        using var db = database.NewContext();

        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.NotEmpty(db.Database.GetAppliedMigrations());
    }
}
