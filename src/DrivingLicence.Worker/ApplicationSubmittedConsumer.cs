using DrivingLicence.Domain;
using DrivingLicence.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DrivingLicence.Worker;

/// <summary>Consumes submitted applications and executes the asynchronous validation and risk workflow.</summary>
public sealed class ApplicationSubmittedConsumer(AppDbContext db, IOptions<WorkflowOptions> options, ILogger<ApplicationSubmittedConsumer> logger) : IConsumer<ApplicationSubmitted>
{
    public async Task Consume(ConsumeContext<ApplicationSubmitted> context)
    {
        var message = context.Message;
        // RabbitMQ delivery is at-least-once. The stable message ID makes redelivery safe after crashes or publish retries.
        if (await db.ProcessedMessages.AnyAsync(x => x.MessageId == message.MessageId, context.CancellationToken)) { logger.LogInformation("Ignoring duplicate message {MessageId}", message.MessageId); return; }
        var app = await db.Applications.Include(x => x.History).SingleOrDefaultAsync(x => x.Id == message.ApplicationId, context.CancellationToken)
            ?? throw new InvalidOperationException($"Application {message.ApplicationId} was not found");
        if (app.Status != ApplicationStatus.Submitted) { await MarkProcessed(message.MessageId, context.CancellationToken); return; }
        var now = DateTimeOffset.UtcNow; var checks = new ApplicationChecks(options.Value); var today = DateOnly.FromDateTime(now.UtcDateTime);
        app.TransitionTo(ApplicationStatus.ValidatingData, "ValidationStarted", null, now);
        var failure = checks.ValidateData(app, today);
        if (failure is not null) app.TransitionTo(ApplicationStatus.Failed, "ValidationFailed", failure, DateTimeOffset.UtcNow);
        else
        {
            app.TransitionTo(ApplicationStatus.CheckingPhoto, "ValidationPassed", null, DateTimeOffset.UtcNow); app.SetPhotoQuality(SimulatedQuality(app.Id)); failure = checks.ValidatePhoto(app);
            if (failure is not null) app.TransitionTo(ApplicationStatus.Failed, "PhotoCheckFailed", failure, DateTimeOffset.UtcNow);
            else
            {
                app.TransitionTo(ApplicationStatus.RiskAssessment, "PhotoCheckPassed", $"Quality score: {app.PhotoQualityScore}", DateTimeOffset.UtcNow);
                var reason = checks.ManualReviewReason(app, today);
                app.TransitionTo(reason is null ? ApplicationStatus.Approved : ApplicationStatus.PendingManualReview, reason is null ? "AutomaticallyApproved" : "ManualReviewRequired", reason, DateTimeOffset.UtcNow);
            }
        }
        // Workflow updates, audit rows, and the processed marker share one DbContext SaveChanges transaction.
        await MarkProcessed(message.MessageId, context.CancellationToken); logger.LogInformation("Application {ApplicationId} processed to {Status}", app.Id, app.Status);
    }
    private async Task MarkProcessed(Guid messageId, CancellationToken ct) { db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, ProcessedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(ct); }
    // Deriving the simulated score from the ID keeps demos and tests reproducible without implying biometric analysis.
    internal static int SimulatedQuality(Guid id) => 30 + Math.Abs(BitConverter.ToInt32(id.ToByteArray(), 0) % 71);
}
