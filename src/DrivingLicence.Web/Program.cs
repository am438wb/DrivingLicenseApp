using DrivingLicence.Web.Components;
using DrivingLicence.Web.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);
// Aspire can launch this project without the Development environment. Explicitly
// enable referenced-library assets so Radzen CSS, fonts, and JavaScript are served.
builder.WebHost.UseStaticWebAssets();
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options => options.DetailedErrors = true);
builder.Services.AddRadzenComponents();
builder.Services.AddHttpClient<ApplicationApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "http://localhost:8080");
    client.DefaultRequestHeaders.Add("X-Reviewer-Key", builder.Configuration["Reviewer:ApiKey"] ?? "dev-reviewer-key");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

app.Run();
