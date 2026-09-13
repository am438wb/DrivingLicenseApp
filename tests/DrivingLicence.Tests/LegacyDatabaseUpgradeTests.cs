using DrivingLicence.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace DrivingLicence.Tests;

[Trait("Category", "Integration")]
/// <summary>Guards the non-destructive upgrade path for databases created before EF migrations were introduced.</summary>
public sealed class LegacyDatabaseUpgradeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("legacy_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public Task InitializeAsync() => database.StartAsync();
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    /// <summary>
    /// Recreates the partially migrated legacy schema seen during development and verifies that bootstrap adopts it,
    /// records the baseline migration, and adds the newer idempotency column without dropping existing tables.
    /// </summary>
    public async Task Bootstrapper_adopts_EnsureCreated_database_without_losing_data()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE applications DROP COLUMN \"IdempotencyKey\";");
        // Reproduce a failed first migration attempt: EF creates the history table
        // before InitialCreate fails because the legacy application tables exist.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
            """);

        await DatabaseBootstrapper.InitialiseAsync(db, CancellationToken.None);

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain("20260911142541_InitialCreate");
        await db.Database.ExecuteSqlRawAsync("SELECT \"IdempotencyKey\" FROM applications LIMIT 0;");
    }
}
