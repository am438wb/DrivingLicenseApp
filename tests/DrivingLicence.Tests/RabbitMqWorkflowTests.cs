using DrivingLicence.Domain;
using DrivingLicence.Infrastructure;
using DrivingLicence.Worker;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace DrivingLicence.Tests;

/// <summary>Exercises the production outbox and consumer across real PostgreSQL and RabbitMQ containers.</summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqWorkflowTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("licences_messaging_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private readonly RabbitMqContainer rabbitMq = new RabbitMqBuilder("rabbitmq:4.3-management")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();
    private WebApplicationFactory<Program>? apiFactory;
    private IHost? workerHost;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(database.StartAsync(), rabbitMq.StartAsync());

        // The API runs its real MassTransit bus and OutboxDispatcher; only external addresses are replaced.
        apiFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("RabbitIntegration");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = database.GetConnectionString(),
                    ["ConnectionStrings:messaging"] = rabbitMq.GetConnectionString()
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
            });
        });
        _ = apiFactory.Server; // Starts the API, applies migrations, and starts the outbox dispatcher.

        // Run the same consumer registration as the Worker executable against the isolated containers.
        var workerBuilder = Host.CreateApplicationBuilder();
        workerBuilder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
        workerBuilder.Services.Configure<WorkflowOptions>(_ => { });
        workerBuilder.Services.AddMassTransit(configuration =>
        {
            configuration.AddConsumer<ApplicationSubmittedConsumer>();
            configuration.UsingRabbitMq((context, bus) =>
            {
                bus.Host(new Uri(rabbitMq.GetConnectionString()));
                bus.ReceiveEndpoint("driving-licence-applications", endpoint =>
                {
                    endpoint.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)));
                    endpoint.ConfigureConsumer<ApplicationSubmittedConsumer>(context);
                });
            });
        });
        workerHost = workerBuilder.Build();
        await workerHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (workerHost is not null)
        {
            await workerHost.StopAsync();
            workerHost.Dispose();
        }
        if (apiFactory is not null) await apiFactory.DisposeAsync();
        await Task.WhenAll(database.DisposeAsync().AsTask(), rabbitMq.DisposeAsync().AsTask());
    }

    /// <summary>Proves that an outbox row is published, consumed once, and committed as an approved workflow.</summary>
    [Fact]
    public async Task Outbox_message_is_consumed_and_application_reaches_approved()
    {
        var applicationId = GuidWithAutomaticApprovalScore();
        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var app = DrivingLicenceApplication.Create("Queue", "Test", new DateOnly(1990, 1, 1), "QUEUE-TEST",
            "queue@example.com", "+306900000000", "Test Street", "B", "photo.jpg", "image/jpeg", 1_024,
            now, applicationId);

        await using (var scope = apiFactory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Applications.Add(app);
            db.OutboxMessages.Add(new OutboxMessage { Id = messageId, ApplicationId = applicationId, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        DrivingLicenceApplication? persisted = null;
        bool published = false;
        bool processed = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = apiFactory!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            persisted = await db.Applications.AsNoTracking().SingleAsync(x => x.Id == applicationId);
            published = await db.OutboxMessages.AnyAsync(x => x.Id == messageId && x.PublishedAt != null);
            processed = await db.ProcessedMessages.AnyAsync(x => x.MessageId == messageId);
            if (persisted.Status == ApplicationStatus.Approved && published && processed) break;
            await Task.Delay(250);
        }

        persisted!.Status.Should().Be(ApplicationStatus.Approved);
        published.Should().BeTrue("the API outbox dispatcher should mark the RabbitMQ publication");
        processed.Should().BeTrue("the Worker should persist its idempotency marker");
    }

    private static Guid GuidWithAutomaticApprovalScore()
    {
        while (true)
        {
            var id = Guid.NewGuid();
            var score = 30 + Math.Abs(BitConverter.ToInt32(id.ToByteArray(), 0) % 71);
            if (score >= 70) return id;
        }
    }
}
