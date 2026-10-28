using System.Runtime.CompilerServices;
using AspirePayments.Api.Endpoints;
using AspirePayments.Api.Messaging;
using AspirePayments.Data;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Messaging;
using AspirePayments.Data.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Documento OpenAPI (Development). Sem este registro, MapOpenApi() não
// encontra os serviços e o endpoint responde 404.
builder.Services.AddOpenApi();

// Observabilidade, health checks e service discovery (compartilhado com o worker).
builder.AddServiceDefaults();

// Mensageria: registra o IConnection (RabbitMQ.Client v7) usando a connection
// string "messaging" injetada pelo Aspire via WithReference(rabbitmq).
builder.AddRabbitMQClient("messaging");

var postgresConnection = builder.Configuration.GetConnectionString("postgres")
    ?? throw new InvalidOperationException(
        "Connection string 'postgres' not found. The API expects it to be injected by Aspire.");

builder.Services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(postgresConnection));

builder.Services.Configure<PaymentGatewayOptions>(
    builder.Configuration.GetSection(PaymentGatewayOptions.SectionName));

// O gateway recebe a options concreta, não IOptions<T>.
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IOptions<PaymentGatewayOptions>>().Value);

builder.Services.Configure<OutboxOptions>(
    builder.Configuration.GetSection(OutboxOptions.SectionName));

builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
builder.Services.AddSingleton<RabbitMqMessagePublisher>();

// Hub de notificações que alimenta o stream SSE do frontend.
builder.Services.AddSingleton<EventChannel>();

builder.Services.AddScoped<OrderPaymentService>();
builder.Services.AddScoped<RefundService>();

// Publica as mensagens da outbox no RabbitMQ.
builder.Services.AddHostedService<OutboxPublisher>();

// Consome as notificações de status e as entrega aos clientes SSE.
builder.Services.AddHostedService<PaymentNotificationConsumer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapDefaultEndpoints();

// Aplica as migrações antes de começar a atender requisições: o health check
// só fica verde quando o banco está pronto.
await ApplyMigrationsAsync(app);

app.MapPaymentEndpoints();

// Stream SSE com as mudanças de status de pagamento.
app.MapGet("/api/events", (
    EventChannel events,
    CancellationToken cancellationToken) =>
{
    return TypedResults.ServerSentEvents(
        StreamPaymentEvents(events, cancellationToken),
        eventType: "status");
})
.WithName("GetPaymentEvents")
.WithTags("Payments")
.WithSummary("Streams payment status changes over Server-Sent Events.");

app.Run();

static async IAsyncEnumerable<PaymentStatusChanged> StreamPaymentEvents(
    EventChannel events,
    [EnumeratorCancellation]
    CancellationToken cancellationToken)
{
    var subscription = events.Subscribe();

    try
    {
        await foreach (var notification in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            yield return notification;
        }
    }
    finally
    {
        events.Unsubscribe(subscription);
    }
}

static async Task ApplyMigrationsAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();

    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Applying database migrations...");

    await db.Database.MigrateAsync();

    logger.LogInformation("Database migrations applied.");
}
