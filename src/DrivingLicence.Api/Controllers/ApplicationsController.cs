using System.ComponentModel.DataAnnotations;
using DrivingLicence.Domain;
using DrivingLicence.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace DrivingLicence.Api.Controllers;

/// <summary>Accepts applications, exposes workflow state and audit history, and handles manual-review decisions.</summary>
[ApiController, Route("api/applications")]
public sealed class ApplicationsController(AppDbContext db, IConfiguration configuration, ILogger<ApplicationsController> logger) : ControllerBase
{
    /// <summary>Submits a driving-licence application for asynchronous processing.</summary>
    /// <remarks>
    /// The personal data, licence category, and face photo are sent as multipart/form-data.
    /// Supply an optional <c>Idempotency-Key</c> header to safely retry the submission without creating a duplicate.
    /// The returned Submitted status is an initial state; processing continues through RabbitMQ and the worker.
    /// </remarks>
    /// <response code="202">The application was stored and queued for asynchronous processing.</response>
    /// <response code="400">The request, photo, or idempotency key is invalid.</response>
    [HttpPost, Consumes("multipart/form-data"), RequestSizeLimit(5_000_000)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit([FromForm] SubmitApplicationRequest request, CancellationToken ct)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (idempotencyKey?.Length > 100) return BadRequest(new ProblemDetails { Title = "Invalid idempotency key", Detail = "Idempotency-Key cannot exceed 100 characters." });
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return AcceptedAtAction(nameof(Get), new { applicationId = existing.Id }, new { applicationId = existing.Id, status = existing.Status.ToString() });
        }
        if (request.Photo is null || request.Photo.Length == 0) return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["photo"] = ["A face photo is required."] }));
        var id = Guid.NewGuid(); var root = configuration["PhotoStorage:Path"] ?? Path.Combine(AppContext.BaseDirectory, "photos"); Directory.CreateDirectory(root);
        var relativePath = $"{id:N}{Path.GetExtension(request.Photo.FileName).ToLowerInvariant()}";
        await using (var stream = System.IO.File.Create(Path.Combine(root, relativePath))) await request.Photo.CopyToAsync(stream, ct);
        var now = DateTimeOffset.UtcNow;
        var application = DrivingLicenceApplication.Create(request.FirstName, request.LastName, request.DateOfBirth, request.NationalId, request.Email,
            request.PhoneNumber, request.Address, request.LicenceCategory, relativePath, request.Photo.ContentType, request.Photo.Length, now, id, idempotencyKey);
        db.Applications.Add(application); db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), ApplicationId = id, CreatedAt = now });
        await db.SaveChangesAsync(ct); logger.LogInformation("Application {ApplicationId} submitted", id);
        return AcceptedAtAction(nameof(Get), new { applicationId = id }, new { applicationId = id, status = application.Status.ToString() });
    }

    /// <summary>Gets the current workflow status of an application.</summary>
    /// <response code="200">The application status and timestamps.</response>
    /// <response code="404">No application exists with the supplied ID.</response>
    [HttpGet("{applicationId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid applicationId, CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, ct);
        return app is null ? NotFound() : Ok(new { applicationId = app.Id, status = app.Status.ToString(), app.CreatedAt, app.UpdatedAt });
    }

    /// <summary>Gets the chronological audit trail for an application.</summary>
    /// <remarks>Each entry records the previous state, new state, event name, timestamp, and optional decision details.</remarks>
    /// <response code="200">The ordered workflow transition history.</response>
    /// <response code="404">No application exists with the supplied ID.</response>
    [HttpGet("{applicationId:guid}/history")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> History(Guid applicationId, CancellationToken ct)
    {
        if (!await db.Applications.AnyAsync(x => x.Id == applicationId, ct)) return NotFound();
        var result = await db.WorkflowHistory.AsNoTracking().Where(x => x.ApplicationId == applicationId).OrderBy(x => x.Timestamp).ThenBy(x => x.Id)
            .Select(x => new { x.Timestamp, FromStatus = x.FromStatus == null ? null : x.FromStatus.ToString(), ToStatus = x.ToStatus.ToString(), x.Event, x.Details }).ToListAsync(ct);
        return Ok(result);
    }

    /// <summary>Approves an application awaiting manual review.</summary>
    /// <remarks>Requires a valid <c>X-Reviewer-Key</c> header. The decision is added to the audit trail.</remarks>
    /// <response code="200">The application was approved.</response>
    /// <response code="401">The reviewer key is missing or invalid.</response>
    /// <response code="404">No application exists with the supplied ID.</response>
    /// <response code="409">The application is not currently pending manual review.</response>
    [HttpPost("{applicationId:guid}/approve")]
    [ServiceFilter(typeof(ReviewerApiKeyFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Approve(Guid applicationId, CancellationToken ct) => ManualTransition(applicationId, ApplicationStatus.Approved, "ManuallyApproved", null, ct);

    /// <summary>Rejects an application awaiting manual review and records the reason.</summary>
    /// <remarks>Requires a valid <c>X-Reviewer-Key</c> header. The reason must contain between 3 and 500 characters.</remarks>
    /// <response code="200">The application was rejected.</response>
    /// <response code="400">The rejection reason is invalid.</response>
    /// <response code="401">The reviewer key is missing or invalid.</response>
    /// <response code="404">No application exists with the supplied ID.</response>
    /// <response code="409">The application is not currently pending manual review.</response>
    [HttpPost("{applicationId:guid}/reject")]
    [ServiceFilter(typeof(ReviewerApiKeyFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Reject(Guid applicationId, [FromBody] RejectRequest request, CancellationToken ct) => ManualTransition(applicationId, ApplicationStatus.Rejected, "ManuallyRejected", request.Reason, ct);

    private async Task<IActionResult> ManualTransition(Guid id, ApplicationStatus status, string @event, string? details, CancellationToken ct)
    {
        var app = await db.Applications.SingleOrDefaultAsync(x => x.Id == id, ct); if (app is null) return NotFound();
        if (app.Status != ApplicationStatus.PendingManualReview) return Conflict(new ProblemDetails { Title = "Invalid workflow transition", Detail = $"Only PendingManualReview applications can be manually reviewed; current status is {app.Status}.", Status = 409 });
        app.TransitionTo(status, @event, details, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
        return Ok(new { applicationId = id, status = status.ToString() });
    }
}
/// <summary>Multipart form fields required to create a driving-licence application.</summary>
public sealed class SubmitApplicationRequest
{
    /// <summary>Applicant's given name.</summary>
    [Required] public string FirstName { get; init; } = "";
    /// <summary>Applicant's family name.</summary>
    [Required] public string LastName { get; init; } = "";
    /// <summary>Date of birth in yyyy-MM-dd format. Applicants must be at least 18.</summary>
    [Required] public DateOnly DateOfBirth { get; init; }
    /// <summary>Government-issued national identifier.</summary>
    [Required] public string NationalId { get; init; } = "";
    /// <summary>Applicant's email address.</summary>
    [Required, EmailAddress] public string Email { get; init; } = "";
    /// <summary>Applicant's contact telephone number.</summary>
    [Required] public string PhoneNumber { get; init; } = "";
    /// <summary>Applicant's residential address.</summary>
    [Required] public string Address { get; init; } = "";
    /// <summary>Requested licence category, for example B.</summary>
    [Required] public string LicenceCategory { get; init; } = "";
    /// <summary>JPEG or PNG face photo, with a maximum HTTP request size of 5 MB.</summary>
    [Required] public IFormFile? Photo { get; init; }
}

/// <summary>Reason supplied when a reviewer rejects an application.</summary>
/// <param name="Reason">Human-readable rejection reason containing 3 to 500 characters.</param>
public sealed record RejectRequest([Required, MinLength(3), MaxLength(500)] string Reason);

public sealed class ReviewerApiKeyFilter(IConfiguration configuration) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // This lightweight API-key guard is intentionally local-demo authentication; production should use an identity provider and reviewer role.
        var expected = configuration["Reviewer:ApiKey"];
        var supplied = context.HttpContext.Request.Headers["X-Reviewer-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(expected) || !string.Equals(expected, supplied, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedObjectResult(new ProblemDetails { Title = "Reviewer authentication required", Detail = "Supply a valid X-Reviewer-Key header.", Status = 401 });
            return;
        }
        await next();
    }
}
