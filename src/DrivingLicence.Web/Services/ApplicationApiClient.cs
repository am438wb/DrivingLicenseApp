using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DrivingLicence.Web.Services;

public sealed class ApplicationApiClient(HttpClient http)
{
    public async Task<ApplicationSummary> SubmitAsync(NewApplication application, Stream photo, string fileName, string contentType, string? idempotencyKey = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(application.FirstName), "firstName");
        form.Add(new StringContent(application.LastName), "lastName");
        form.Add(new StringContent(application.DateOfBirth.ToString("yyyy-MM-dd")), "dateOfBirth");
        form.Add(new StringContent(application.NationalId), "nationalId");
        form.Add(new StringContent(application.Email), "email");
        form.Add(new StringContent(application.PhoneNumber), "phoneNumber");
        form.Add(new StringContent(application.Address), "address");
        form.Add(new StringContent(application.LicenceCategory), "licenceCategory");
        var file = new StreamContent(photo); file.Headers.ContentType = new MediaTypeHeaderValue(contentType); form.Add(file, "photo", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/applications") { Content = form };
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ApplicationSummary>(cancellationToken: ct))!;
    }

    public async Task<ApplicationSummary?> GetAsync(Guid id, CancellationToken ct = default)
        => await http.GetFromJsonAsync<ApplicationSummary>($"api/applications/{id}", ct);

    public async Task<List<HistoryEntry>> GetHistoryAsync(Guid id, CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<HistoryEntry>>($"api/applications/{id}/history", ct) ?? [];

    public Task<ApplicationSummary> ApproveAsync(Guid id, CancellationToken ct = default) => DecisionAsync(id, "approve", null, ct);
    public Task<ApplicationSummary> RejectAsync(Guid id, string reason, CancellationToken ct = default) => DecisionAsync(id, "reject", new { reason }, ct);

    private async Task<ApplicationSummary> DecisionAsync(Guid id, string action, object? body, CancellationToken ct)
    {
        using var response = body is null ? await http.PostAsync($"api/applications/{id}/{action}", null, ct) : await http.PostAsJsonAsync($"api/applications/{id}/{action}", body, ct);
        await EnsureSuccess(response, ct); return (await response.Content.ReadFromJsonAsync<ApplicationSummary>(cancellationToken: ct))!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"API returned {(int)response.StatusCode}: {detail}");
    }
}

public sealed class NewApplication
{
    public string FirstName { get; set; } = ""; public string LastName { get; set; } = "";
    public DateTime DateOfBirth { get; set; } = DateTime.Today.AddYears(-25);
    public string NationalId { get; set; } = ""; public string Email { get; set; } = "";
    public string PhoneNumber { get; set; } = ""; public string Address { get; set; } = "";
    public string LicenceCategory { get; set; } = "B";
}
public sealed record ApplicationSummary(Guid ApplicationId, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record HistoryEntry(DateTimeOffset Timestamp, string? FromStatus, string ToStatus, string Event, string? Details);
