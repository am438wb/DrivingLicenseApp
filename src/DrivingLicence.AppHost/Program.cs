var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("Database", "licences");

// Use predictable credentials for this local technical-assignment environment so the
// management UI is immediately accessible. Production credentials must come from a secret store.
var rabbitUser = builder.AddParameter("rabbitmq-username", "guest", publishValueAsDefault: false, secret: false);
var rabbitPassword = builder.AddParameter("rabbitmq-password", "guest", publishValueAsDefault: false, secret: true);
var rabbit = builder.AddRabbitMQ("messaging", rabbitUser, rabbitPassword)
    .WithManagementPlugin();

var api = builder.AddProject<Projects.DrivingLicence_Api>("api")
    .WithReference(postgres)
    .WithReference(rabbit);

builder.AddProject<Projects.DrivingLicence_Worker>("worker")
    .WithReference(postgres)
    .WithReference(rabbit);

builder.AddProject<Projects.DrivingLicence_Web>("web")
    .WithReference(api)
    .WithEnvironment("ApiBaseUrl", api.GetEndpoint("http"));

builder.Build().Run();
