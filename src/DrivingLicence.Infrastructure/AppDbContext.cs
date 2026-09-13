using DrivingLicence.Domain;
using Microsoft.EntityFrameworkCore;

namespace DrivingLicence.Infrastructure;

/// <summary>EF Core unit of work for applications, workflow history, outbox, and message deduplication.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DrivingLicenceApplication> Applications => Set<DrivingLicenceApplication>();
    public DbSet<WorkflowHistory> WorkflowHistory => Set<WorkflowHistory>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var app = modelBuilder.Entity<DrivingLicenceApplication>();
        app.ToTable("applications").HasKey(x => x.Id);
        app.Property(x => x.Status).HasConversion<string>();
        app.Property(x => x.DateOfBirth).HasColumnType("date");
        app.Property(x => x.IdempotencyKey).HasMaxLength(100);
        app.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        app.Property(x => x.Version).IsRowVersion();
        app.HasMany(x => x.History).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        app.Navigation(x => x.History).UsePropertyAccessMode(PropertyAccessMode.Property);

        var history = modelBuilder.Entity<WorkflowHistory>();
        history.ToTable("workflow_history").HasKey(x => x.Id);
        history.Property(x => x.FromStatus).HasConversion<string>();
        history.Property(x => x.ToStatus).HasConversion<string>();
        history.HasIndex(x => new { x.ApplicationId, x.Timestamp });

        modelBuilder.Entity<OutboxMessage>().ToTable("outbox_messages").HasKey(x => x.Id);
        modelBuilder.Entity<OutboxMessage>().HasIndex(x => x.PublishedAt);
        modelBuilder.Entity<ProcessedMessage>().ToTable("processed_messages").HasKey(x => x.MessageId);
    }
}

/// <summary>Durable queue-publication request committed atomically with an application.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Inbox/deduplication marker for an already-consumed queue message.</summary>
public sealed class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Applies migrations safely at startup and adopts databases created by the earlier EnsureCreated version.</summary>
public static class DatabaseBootstrapper
{
    public static async Task InitialiseAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.OpenConnectionAsync(cancellationToken);
                // API and Worker can start concurrently; the PostgreSQL advisory lock serialises schema migration.
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(72419031);", cancellationToken);
                try
                {
                    // Preserve existing exercise data while baselining the legacy schema into EF migration history.
                    if (await IsLegacyEnsureCreatedDatabase(db, cancellationToken))
                    {
                        await db.Database.ExecuteSqlRawAsync("""
                            ALTER TABLE applications ADD COLUMN IF NOT EXISTS "IdempotencyKey" character varying(100) NULL;
                            CREATE UNIQUE INDEX IF NOT EXISTS "IX_applications_IdempotencyKey"
                                ON applications ("IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL;
                            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                                 "MigrationId" character varying(150) NOT NULL,
                                 "ProductVersion" character varying(32) NOT NULL,
                                 CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
                            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                                VALUES ('20260911142541_InitialCreate', '8.0.11')
                                ON CONFLICT ("MigrationId") DO NOTHING;
                            """, cancellationToken);
                    }

                    await db.Database.MigrateAsync(cancellationToken);
                    return;
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(72419031);", cancellationToken);
                    await db.Database.CloseConnectionAsync();
                }
            }
            catch when (attempt < 10) { await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 10)), cancellationToken); }
        }
    }

    private static async Task<bool> IsLegacyEnsureCreatedDatabase(AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await RelationExists(db, "applications", cancellationToken)) return false;
        if (!await RelationExists(db, "__EFMigrationsHistory", cancellationToken)) return true;

        await using var historyCommand = db.Database.GetDbConnection().CreateCommand();
        historyCommand.CommandText = """
            SELECT NOT EXISTS (
                SELECT 1 FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20260911142541_InitialCreate');
            """;
        return (bool)(await historyCommand.ExecuteScalarAsync(cancellationToken) ?? true);
    }

    private static async Task<bool> RelationExists(AppDbContext db, string relationName, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT to_regclass('public.' || quote_ident(@name)) IS NOT NULL;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = relationName;
        command.Parameters.Add(parameter);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }
}
