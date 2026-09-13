using System.Net.Mail;

namespace DrivingLicence.Domain;

/// <summary>States through which a driving-licence application can progress.</summary>
public enum ApplicationStatus { Submitted, ValidatingData, CheckingPhoto, RiskAssessment, PendingManualReview, Approved, Rejected, Failed }

/// <summary>Aggregate root that owns application state and its workflow audit history.</summary>
public sealed class DrivingLicenceApplication
{
    private DrivingLicenceApplication() { }
    public Guid Id { get; private set; }
    public string FirstName { get; private set; } = "";
    public string LastName { get; private set; } = "";
    public DateOnly DateOfBirth { get; private set; }
    public string NationalId { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string PhoneNumber { get; private set; } = "";
    public string Address { get; private set; } = "";
    public string LicenceCategory { get; private set; } = "";
    public string PhotoPath { get; private set; } = "";
    public string PhotoContentType { get; private set; } = "";
    public long PhotoSize { get; private set; }
    public int? PhotoQualityScore { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public ApplicationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }
    public List<WorkflowHistory> History { get; private set; } = [];

    public static DrivingLicenceApplication Create(string firstName, string lastName, DateOnly dob, string nationalId,
        string email, string phone, string address, string category, string photoPath, string contentType, long photoSize, DateTimeOffset now, Guid? id = null, string? idempotencyKey = null)
    {
        var app = new DrivingLicenceApplication { Id = id ?? Guid.NewGuid(), FirstName = firstName.Trim(), LastName = lastName.Trim(),
            DateOfBirth = dob, NationalId = nationalId.Trim(), Email = email.Trim(), PhoneNumber = phone.Trim(), Address = address.Trim(),
            LicenceCategory = category.Trim().ToUpperInvariant(), PhotoPath = photoPath, PhotoContentType = contentType, PhotoSize = photoSize,
            Status = ApplicationStatus.Submitted, CreatedAt = now, UpdatedAt = now, IdempotencyKey = idempotencyKey };
        app.History.Add(WorkflowHistory.Create(app.Id, null, app.Status, "ApplicationCreated", null, now));
        return app;
    }

    public void TransitionTo(ApplicationStatus next, string @event, string? details, DateTimeOffset now)
    {
        // Centralising transitions here prevents callers from bypassing workflow rules and guarantees an audit entry.
        if (!WorkflowStateMachine.CanTransition(Status, next))
            throw new InvalidWorkflowTransitionException(Status, next);
        var previous = Status; Status = next; UpdatedAt = now;
        History.Add(WorkflowHistory.Create(Id, previous, next, @event, details, now));
    }

    public void SetPhotoQuality(int score) => PhotoQualityScore = Math.Clamp(score, 0, 100);
}

/// <summary>Immutable-in-practice audit record for one workflow state transition.</summary>
public sealed class WorkflowHistory
{
    private WorkflowHistory() { }
    public long Id { get; private set; }
    public Guid ApplicationId { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public ApplicationStatus? FromStatus { get; private set; }
    public ApplicationStatus ToStatus { get; private set; }
    public string Event { get; private set; } = "";
    public string? Details { get; private set; }
    public static WorkflowHistory Create(Guid applicationId, ApplicationStatus? from, ApplicationStatus to, string @event, string? details, DateTimeOffset now) =>
        new() { ApplicationId = applicationId, FromStatus = from, ToStatus = to, Event = @event, Details = details, Timestamp = now };
}

/// <summary>Defines the only legal state changes for the onboarding workflow.</summary>
public static class WorkflowStateMachine
{
    private static readonly IReadOnlyDictionary<ApplicationStatus, ApplicationStatus[]> Allowed = new Dictionary<ApplicationStatus, ApplicationStatus[]>
    {
        [ApplicationStatus.Submitted] = [ApplicationStatus.ValidatingData],
        [ApplicationStatus.ValidatingData] = [ApplicationStatus.CheckingPhoto, ApplicationStatus.Failed],
        [ApplicationStatus.CheckingPhoto] = [ApplicationStatus.RiskAssessment, ApplicationStatus.Failed],
        [ApplicationStatus.RiskAssessment] = [ApplicationStatus.PendingManualReview, ApplicationStatus.Approved, ApplicationStatus.Rejected, ApplicationStatus.Failed],
        [ApplicationStatus.PendingManualReview] = [ApplicationStatus.Approved, ApplicationStatus.Rejected]
    };
    public static bool CanTransition(ApplicationStatus from, ApplicationStatus to) => Allowed.TryGetValue(from, out var next) && next.Contains(to);
}

public sealed class InvalidWorkflowTransitionException(ApplicationStatus from, ApplicationStatus to)
    : InvalidOperationException($"Cannot transition application from {from} to {to}.");

/// <summary>Configurable thresholds and watchlist values used by the worker's simulated checks.</summary>
public sealed record WorkflowOptions
{
    public int MinimumPhotoBytes { get; init; } = 100;
    public int MinimumPhotoQuality { get; init; } = 40;
    public int ManualReviewPhotoQuality { get; init; } = 70;
    public string[] WatchlistNationalIds { get; init; } = [];
    public string[] SupportedPhotoTypes { get; init; } = ["image/jpeg", "image/png"];
}

/// <summary>Pure, deterministic business rules for data, photo, and risk assessment.</summary>
public sealed class ApplicationChecks(WorkflowOptions options)
{
    public string? ValidateData(DrivingLicenceApplication app, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(app.FirstName) || string.IsNullOrWhiteSpace(app.LastName) || string.IsNullOrWhiteSpace(app.NationalId)) return "Required personal data is missing";
        try { _ = new MailAddress(app.Email); } catch { return "Email is invalid"; }
        if (Age(app.DateOfBirth, today) < 18) return "Applicant is under 18";
        return null;
    }

    public string? ValidatePhoto(DrivingLicenceApplication app)
    {
        if (string.IsNullOrWhiteSpace(app.PhotoPath)) return "No photo was provided";
        if (!options.SupportedPhotoTypes.Contains(app.PhotoContentType, StringComparer.OrdinalIgnoreCase)) return "Photo file type is unsupported";
        if (app.PhotoSize < options.MinimumPhotoBytes) return "Photo file is too small";
        if (app.PhotoQualityScore < options.MinimumPhotoQuality) return "Simulated photo quality is too low";
        return null;
    }

    public string? ManualReviewReason(DrivingLicenceApplication app, DateOnly today)
    {
        if (Age(app.DateOfBirth, today) < 21) return "Applicant is below 21";
        if (!app.LicenceCategory.Equals("B", StringComparison.OrdinalIgnoreCase)) return "Licence category is not B";
        if (options.WatchlistNationalIds.Contains(app.NationalId, StringComparer.OrdinalIgnoreCase)) return "National ID is on the watchlist";
        if (app.PhotoQualityScore < options.ManualReviewPhotoQuality) return "Photo quality requires manual review";
        return null;
    }

    private static int Age(DateOnly dob, DateOnly today) { var age = today.Year - dob.Year; if (dob > today.AddYears(-age)) age--; return age; }
}

/// <summary>Queue contract identifying the submitted application and stable message ID used for deduplication.</summary>
public sealed record ApplicationSubmitted(Guid ApplicationId, Guid MessageId);
