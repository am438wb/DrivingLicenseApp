using DrivingLicence.Api;
using DrivingLicence.Api.Controllers;
using DrivingLicence.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Logging.AddJsonConsole();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Surface controller and request-model XML documentation in Swagger UI.
    var xmlFile = $"{typeof(Program).Assembly.GetName().Name}.xml";
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFile));
});
builder.Services.AddProblemDetails();
builder.Services.AddScoped<ReviewerApiKeyFilter>();
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddMassTransit(x => x.UsingRabbitMq((context, cfg) =>
    {
        var connection = builder.Configuration.GetConnectionString("messaging");
        if (connection is not null) cfg.Host(new Uri(connection));
        else cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        { h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest"); h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest"); });
    }));
    builder.Services.AddHostedService<OutboxDispatcher>();
}
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("postgresql", tags: ["ready"]);
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    await DatabaseBootstrapper.InitialiseAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), CancellationToken.None);
app.UseExceptionHandler(); app.UseSwagger(); app.UseSwaggerUI(); app.MapControllers(); app.MapDefaultEndpoints(); app.Run();
public partial class Program;
