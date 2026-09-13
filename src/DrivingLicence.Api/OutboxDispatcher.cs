using DrivingLicence.Domain;
using DrivingLicence.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace DrivingLicence.Api;

/// <summary>Reliably publishes committed application submissions from the database outbox to RabbitMQ.</summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopeFactory, IBus bus, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // A database row is the durable hand-off between the HTTP transaction and the queue.
                // If publishing fails, PublishedAt remains null and a later cycle retries the same stable message ID.
                var batch = await db.OutboxMessages.Where(x => x.PublishedAt == null).OrderBy(x => x.CreatedAt).Take(20).ToListAsync(stoppingToken);
                foreach (var item in batch)
                {
                    try { await bus.Publish(new ApplicationSubmitted(item.ApplicationId, item.Id), stoppingToken); item.PublishedAt = DateTimeOffset.UtcNow; }
                    catch (Exception ex) { item.Attempts++; item.LastError = ex.Message[..Math.Min(ex.Message.Length, 1000)]; logger.LogWarning(ex, "Failed to publish outbox message {MessageId}", item.Id); }
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) { logger.LogError(ex, "Outbox dispatch cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
