using DrivingLicence.Domain;
using DrivingLicence.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace DrivingLicence.Tests;

[Trait("Category", "Integration")]
/// <summary>Validates the EF Core model and transaction boundaries against a real PostgreSQL instance.</summary>
public sealed class PostgreSqlPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("licences_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public Task InitializeAsync() => database.StartAsync();
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    /// <summary>
    /// Proves that the application, transition history, idempotency key, and outbox record can be committed
    /// together using the production database provider and checked-in migration.
    /// </summary>
    public async Task Migration_persists_application_audit_and_outbox_atomically()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using (var setup = new AppDbContext(options)) await setup.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var application = DrivingLicenceApplication.Create("Ada", "Lovelace", new DateOnly(1990, 12, 10), "INT-001",
            "ada@example.com", "+306900000001", "Test Street 1", "B", "photo.jpg", "image/jpeg", 1_024, now,
            idempotencyKey: "integration-request-1");
        application.TransitionTo(ApplicationStatus.ValidatingData, "ValidationStarted", null, now.AddSeconds(1));
        var outbox = new OutboxMessage { Id = Guid.NewGuid(), ApplicationId = application.Id, CreatedAt = now };

        await using (var write = new AppDbContext(options))
        {
            write.Applications.Add(application);
            write.OutboxMessages.Add(outbox);
            await write.SaveChangesAsync();
        }

        await using var read = new AppDbContext(options);
        var persisted = await read.Applications.Include(x => x.History).SingleAsync(x => x.Id == application.Id);
        persisted.Status.Should().Be(ApplicationStatus.ValidatingData);
        persisted.History.Should().HaveCount(2);
        persisted.IdempotencyKey.Should().Be("integration-request-1");
        (await read.OutboxMessages.AnyAsync(x => x.ApplicationId == application.Id)).Should().BeTrue();
    }
}
