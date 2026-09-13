using DrivingLicence.Domain;
using DrivingLicence.Infrastructure;
using DrivingLicence.Worker;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Logging.AddJsonConsole();
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.Configure<WorkflowOptions>(builder.Configuration.GetSection("Workflow"));
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ApplicationSubmittedConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        var connection = builder.Configuration.GetConnectionString("messaging");
        if (connection is not null) cfg.Host(new Uri(connection));
        else cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h => { h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest"); h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest"); });
        cfg.ReceiveEndpoint("driving-licence-applications", e => { e.UseMessageRetry(r => r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2))); e.ConfigureConsumer<ApplicationSubmittedConsumer>(context); });
    });
});
var host = builder.Build();
using (var scope = host.Services.CreateScope()) await DatabaseBootstrapper.InitialiseAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), CancellationToken.None);
await host.RunAsync();
