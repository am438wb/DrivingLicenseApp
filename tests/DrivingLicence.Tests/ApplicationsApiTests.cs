using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DrivingLicence.Domain;
using DrivingLicence.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace DrivingLicence.Tests;

[Trait("Category", "Integration")]
/// <summary>
/// Exercises the real ASP.NET Core routing, model binding, validation, authorization filter, controller behavior,
/// and PostgreSQL persistence through HTTP. RabbitMQ dispatch is disabled so endpoint failures remain isolated.
/// </summary>
public sealed class ApplicationsApiTests : IAsyncLifetime
{
    private const string ReviewerKey = "integration-reviewer-key";
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("licences_api_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private WebApplicationFactory<Program>? factory;
    private HttpClient Client => factory!.CreateClient();

    public async Task InitializeAsync()
    {
        // Each test class gets an isolated real PostgreSQL database; the API itself production registrations are retained.
        await database.StartAsync();
        var photoPath = Path.Combine(Path.GetTempPath(), $"driving-licence-api-tests-{Guid.NewGuid():N}");
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = database.GetConnectionString(),
                    ["Reviewer:ApiKey"] = ReviewerKey,
                    ["PhotoStorage:Path"] = photoPath
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
            });
        });

        // Force application startup and its normal migration bootstrap before the first request.
        _ = factory.Server;
    }

    public async Task DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    /// <summary>Verifies the public multipart contract, 202 response, persistence, and status lookup.</summary>
    public async Task Submit_returns_accepted_and_can_be_retrieved()
    {
        using var request = ValidSubmission();
        request.Headers.Add("Idempotency-Key", "api-test-submit-1");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var submitted = await ReadJson(response);
        submitted.GetProperty("status").GetString().Should().Be("Submitted");
        var id = submitted.GetProperty("applicationId").GetGuid();
        (await Client.GetAsync($"/api/applications/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Verifies that retrying a submission with the same idempotency key returns the original application.</summary>
    [Fact]
    public async Task Submit_with_same_idempotency_key_does_not_create_duplicate()
    {
        var key = $"api-test-idempotency-{Guid.NewGuid():N}";
        using var firstRequest = ValidSubmission();
        firstRequest.Headers.Add("Idempotency-Key", key);
        using var firstResponse = await Client.SendAsync(firstRequest);
        var first = await ReadJson(firstResponse);

        using var retryRequest = ValidSubmission();
        retryRequest.Headers.Add("Idempotency-Key", key);
        using var retryResponse = await Client.SendAsync(retryRequest);
        var retry = await ReadJson(retryResponse);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retryResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retry.GetProperty("applicationId").GetGuid().Should().Be(first.GetProperty("applicationId").GetGuid());

        await using var scope = factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Applications.CountAsync(x => x.IdempotencyKey == key)).Should().Be(1);
        (await db.OutboxMessages.CountAsync(x => x.ApplicationId == first.GetProperty("applicationId").GetGuid())).Should().Be(1);
    }

    [Fact]
    /// <summary>Verifies that API-controller model validation rejects malformed email before persistence.</summary>
    public async Task Submit_with_invalid_email_returns_bad_request()
    {
        using var request = ValidSubmission(email: "not-an-email");
        (await Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    /// <summary>Verifies consistent not-found semantics for both status and audit resources.</summary>
    public async Task Unknown_application_and_history_return_not_found()
    {
        var id = Guid.NewGuid();
        (await Client.GetAsync($"/api/applications/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.GetAsync($"/api/applications/{id}/history")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    /// <summary>Verifies that manual decisions cannot be performed anonymously.</summary>
    public async Task Manual_review_requires_reviewer_key()
    {
        var id = await AddApplication(ApplicationStatus.PendingManualReview);
        (await Client.PostAsync($"/api/applications/{id}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    /// <summary>Verifies that authentication does not allow a reviewer to bypass state-machine preconditions.</summary>
    public async Task Manual_review_from_wrong_state_returns_conflict()
    {
        var id = await AddApplication(ApplicationStatus.Submitted);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/applications/{id}/approve");
        request.Headers.Add("X-Reviewer-Key", ReviewerKey);
        (await Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>Verifies that an authenticated approval changes status and creates the expected audit entry.</summary>
    [Fact]
    public async Task Approve_updates_status_and_records_audit_history()
    {
        var id = await AddApplication(ApplicationStatus.PendingManualReview);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/applications/{id}/approve");
        request.Headers.Add("X-Reviewer-Key", ReviewerKey);

        using var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(response)).GetProperty("status").GetString().Should().Be("Approved");
        var history = await Client.GetFromJsonAsync<JsonElement>($"/api/applications/{id}/history");
        var last = history.EnumerateArray().Last();
        last.GetProperty("event").GetString().Should().Be("ManuallyApproved");
        last.GetProperty("toStatus").GetString().Should().Be("Approved");
    }

    [Fact]
    /// <summary>Regression test for rejection-request model binding and its minimum reason length.</summary>
    public async Task Reject_validates_reason()
    {
        var id = await AddApplication(ApplicationStatus.PendingManualReview);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/applications/{id}/reject")
        {
            Content = JsonContent.Create(new { reason = "x" })
        };
        request.Headers.Add("X-Reviewer-Key", ReviewerKey);
        (await Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    /// <summary>Verifies the complete rejection contract, including the persisted reviewer reason in audit history.</summary>
    public async Task Reject_updates_status_and_records_reason_in_history()
    {
        var id = await AddApplication(ApplicationStatus.PendingManualReview);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/applications/{id}/reject")
        {
            Content = JsonContent.Create(new { reason = "Photo needs recapture" })
        };
        request.Headers.Add("X-Reviewer-Key", ReviewerKey);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(response)).GetProperty("status").GetString().Should().Be("Rejected");
        var history = await Client.GetFromJsonAsync<JsonElement>($"/api/applications/{id}/history");
        var last = history.EnumerateArray().Last();
        last.GetProperty("event").GetString().Should().Be("ManuallyRejected");
        last.GetProperty("details").GetString().Should().Be("Photo needs recapture");
    }

    private async Task<Guid> AddApplication(ApplicationStatus desiredStatus)
    {
        // Build state through legal domain transitions instead of mutating persistence fields directly.
        // Fixture events are kept in the past so the HTTP decision is chronologically last.
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var app = DrivingLicenceApplication.Create("API", "Test", new DateOnly(1990, 1, 1), Guid.NewGuid().ToString("N"),
            "api@example.com", "+306900000000", "Test Street", "B", "photo.jpg", "image/jpeg", 1_000, now);
        if (desiredStatus == ApplicationStatus.PendingManualReview)
        {
            app.TransitionTo(ApplicationStatus.ValidatingData, "ValidationStarted", null, now.AddSeconds(1));
            app.TransitionTo(ApplicationStatus.CheckingPhoto, "ValidationPassed", null, now.AddSeconds(2));
            app.TransitionTo(ApplicationStatus.RiskAssessment, "PhotoCheckPassed", null, now.AddSeconds(3));
            app.TransitionTo(ApplicationStatus.PendingManualReview, "ManualReviewRequired", "API test", now.AddSeconds(4));
        }

        await using var scope = factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private static HttpRequestMessage ValidSubmission(string email = "john@example.com")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("John"), "firstName" },
            { new StringContent("Doe"), "lastName" },
            { new StringContent("1990-05-20"), "dateOfBirth" },
            { new StringContent(Guid.NewGuid().ToString("N")), "nationalId" },
            { new StringContent(email), "email" },
            { new StringContent("+306900000000"), "phoneNumber" },
            { new StringContent("Example Street 10"), "address" },
            { new StringContent("B"), "licenceCategory" }
        };
        var photo = new ByteArrayContent(Enumerable.Repeat((byte)42, 1_024).ToArray());
        photo.Headers.ContentType = new("image/jpeg");
        form.Add(photo, "photo", "face.jpg");
        return new HttpRequestMessage(HttpMethod.Post, "/api/applications") { Content = form };
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
