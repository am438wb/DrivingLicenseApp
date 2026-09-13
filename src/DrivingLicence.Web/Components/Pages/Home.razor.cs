using DrivingLicence.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Radzen;

namespace DrivingLicence.Web.Components.Pages;

public partial class Home
{
    [Inject] private ApplicationApiClient Api { get; set; } = default!;
    [Inject] private NotificationService Notifications { get; set; } = default!;

    private NewApplication model = new();
    private readonly string[] categories = ["A", "B", "C", "D"];
    private IBrowserFile? photo;
    private int photoPickerKey;
    private bool submitting;
    private bool loading;
    private string lookupId = "";
    private string rejectReason = "";
    private ApplicationSummary? current;
    private List<HistoryEntry> history = [];
    private bool CanReject => !string.IsNullOrWhiteSpace(rejectReason) && rejectReason.Trim().Length >= 3;

    private void OnPhotoChanged(InputFileChangeEventArgs args)
    {
        photo = args.File;
        if (photo.Size <= 5_000_000) return;

        photo = null;
        Notifications.Notify(NotificationSeverity.Warning, "File too large", "Choose an image smaller than 5 MB.");
    }

    private void ClearForm()
    {
        model = new NewApplication();
        photo = null;
        photoPickerKey++;
    }

    private async Task Submit()
    {
        if (photo is null) return;
        submitting = true;

        try
        {
            await using var stream = photo.OpenReadStream(5_000_000);
            current = await Api.SubmitAsync(model, stream, photo.Name, photo.ContentType, Guid.NewGuid().ToString("N"));
            lookupId = current.ApplicationId.ToString();
            await Load();
            Notifications.Notify(NotificationSeverity.Success, "Submitted", $"Application {lookupId} was queued.");
        }
        catch (Exception ex)
        {
            Notifications.Notify(NotificationSeverity.Error, "Submission failed", ex.Message, 8000);
        }
        finally
        {
            submitting = false;
        }
    }

    private async Task Load()
    {
        if (!Guid.TryParse(lookupId, out var id))
        {
            Notifications.Notify(NotificationSeverity.Warning, "Invalid ID", "Enter a valid application GUID.");
            return;
        }

        loading = true;
        try
        {
            current = await Api.GetAsync(id);
            history = await Api.GetHistoryAsync(id);
        }
        catch (Exception ex)
        {
            Notifications.Notify(NotificationSeverity.Error, "Could not load", ex.Message, 8000);
        }
        finally
        {
            loading = false;
        }
    }

    private async Task Approve()
    {
        if (current is null) return;
        try
        {
            current = await Api.ApproveAsync(current.ApplicationId);
            await Load();
        }
        catch (Exception ex)
        {
            Notifications.Notify(NotificationSeverity.Error, "Approval failed", ex.Message);
        }
    }

    private async Task Reject()
    {
        if (current is null) return;
        if (!CanReject)
        {
            Notifications.Notify(NotificationSeverity.Warning, "Reason required", "Enter a rejection reason of at least 3 characters.");
            return;
        }

        try
        {
            current = await Api.RejectAsync(current.ApplicationId, rejectReason.Trim());
            rejectReason = "";
            await Load();
        }
        catch (Exception ex)
        {
            Notifications.Notify(NotificationSeverity.Error, "Rejection failed", ex.Message);
        }
    }

    private static BadgeStyle BadgeFor(string status) => status switch
    {
        "Approved" => BadgeStyle.Success,
        "Rejected" or "Failed" => BadgeStyle.Danger,
        "PendingManualReview" => BadgeStyle.Warning,
        _ => BadgeStyle.Info
    };
}
