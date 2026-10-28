var builder = DistributedApplication.CreateBuilder(args);

var username = builder.AddParameter("rabbitmq-user", secret: true);
var password = builder.AddParameter("rabbitmq-password", secret: true);

var rabbitmq = builder
    .AddRabbitMQ("messaging", username, password)
    .WithManagementPlugin()
    .WithDataVolume();

// Banco de dados gerenciado pelo Aspire (container PostgreSQL). A senha e
// gerada automaticamente e injetada como ConnectionStrings__postgres.
var postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin();

var api = builder
    .AddProject<Projects.AspirePayments_Api>("api")
        .WithReference(rabbitmq)
        .WithReference(postgres)
        .WaitFor(rabbitmq)
        .WaitFor(postgres);

// O worker espera a API porque é ela quem aplica as migrations do banco.
var worker = builder
    .AddProject<Projects.AspirePayments_Worker>("worker")
        .WithReference(rabbitmq)
        .WithReference(postgres)
        .WaitFor(api)
        .WithHttpEndpoint(port: 5180, name: "dev-control", isProxied: false);

builder
    .AddViteApp(
        "web",
        "../AspirePayments.Web",
        runScriptName: "start")
    .WithReference(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
