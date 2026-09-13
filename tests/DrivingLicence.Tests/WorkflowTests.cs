using DrivingLicence.Domain;
using FluentAssertions;

namespace DrivingLicence.Tests;

/// <summary>Fast unit tests for state-machine invariants and deterministic business rules.</summary>
public sealed class WorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 9, 10, 30, 0, TimeSpan.Zero);
    private static DrivingLicenceApplication App(int age = 30, string category = "B", string nationalId = "OK", string type = "image/jpeg", long size = 1000)
        => DrivingLicenceApplication.Create("John", "Doe", DateOnly.FromDateTime(Now.UtcDateTime).AddYears(-age), nationalId, "john@example.com", "+306900000000", "Street 1", category, "photo.jpg", type, size, Now);

    /// <summary>Protects the aggregate from skipping required workflow stages.</summary>
    [Fact] public void Invalid_transition_is_rejected()
    {
        var app = App();
        var action = () => app.TransitionTo(ApplicationStatus.Approved, "Invalid", null, Now);
        action.Should().Throw<InvalidWorkflowTransitionException>();
    }

    /// <summary>Verifies that changing status cannot occur without a corresponding audit entry.</summary>
    [Fact] public void Every_transition_creates_audit_history()
    {
        var app = App(); app.TransitionTo(ApplicationStatus.ValidatingData, "ValidationStarted", null, Now.AddMinutes(1));
        app.History.Should().HaveCount(2); app.History.Last().FromStatus.Should().Be(ApplicationStatus.Submitted); app.History.Last().ToStatus.Should().Be(ApplicationStatus.ValidatingData);
    }

    /// <summary>Verifies the minimum-age business rule at the exact validation boundary.</summary>
    [Fact] public void Under_18_fails_data_validation()
    {
        var result = new ApplicationChecks(new WorkflowOptions()).ValidateData(App(17), DateOnly.FromDateTime(Now.UtcDateTime));
        result.Should().Be("Applicant is under 18");
    }

    /// <summary>Covers each configured condition that diverts an otherwise valid application to manual review.</summary>
    [Theory]
    [InlineData(20, "B", "OK", "Applicant is below 21")]
    [InlineData(30, "A", "OK", "Licence category is not B")]
    [InlineData(30, "B", "WATCH-001", "National ID is on the watchlist")]
    public void Risk_rules_require_manual_review(int age, string category, string nationalId, string reason)
    {
        var app = App(age, category, nationalId); app.SetPhotoQuality(90);
        var checks = new ApplicationChecks(new WorkflowOptions { WatchlistNationalIds = ["WATCH-001"] });
        checks.ManualReviewReason(app, DateOnly.FromDateTime(Now.UtcDateTime)).Should().Be(reason);
    }

    /// <summary>Distinguishes a failed photo check from the higher manual-review quality threshold.</summary>
    [Fact] public void Low_quality_photo_fails_photo_check()
    {
        var app = App(); app.SetPhotoQuality(39);
        new ApplicationChecks(new WorkflowOptions()).ValidatePhoto(app).Should().Be("Simulated photo quality is too low");
    }
}
