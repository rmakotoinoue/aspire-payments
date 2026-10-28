using AspirePayments.Data;
using AspirePayments.Data.Messaging;
using AspirePayments.Data.Payments;
using AspirePayments.Worker;
using AspirePayments.Worker.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Observabilidade, health checks e service discovery (compartilhado com a API).
builder.AddServiceDefaults();

// Mensageria: IConnection do RabbitMQ v7 a partir da connection string
// "messaging" injetada pelo Aspire.
builder.AddRabbitMQClient("messaging");

var postgresConnection = builder.Configuration.GetConnectionString("postgres")
    ?? throw new InvalidOperationException(
        "Connection string 'postgres' not found. The API expects it to be injected by Aspire.");

builder.Services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(postgresConnection));

builder.Services.Configure<PaymentGatewayOptions>(
    builder.Configuration.GetSection(PaymentGatewayOptions.SectionName));

builder.Services.Configure<OutboxOptions>(
    builder.Configuration.GetSection(OutboxOptions.SectionName));

builder.Services.Configure<WorkerOptions>(
    builder.Configuration.GetSection(WorkerOptions.SectionName));

// O consumidor e o gateway recebem as options concretas, não IOptions<T>.
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IOptions<WorkerOptions>>().Value);

builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IOptions<PaymentGatewayOptions>>().Value);

builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
builder.Services.AddSingleton<RabbitMqMessagePublisher>();
builder.Services.AddSingleton<ConsumerControl>();

builder.Services.AddScoped<PaymentProcessor>();
builder.Services.AddScoped<RefundProcessor>();

// Publica as mensagens da outbox (as de pagamento e as de notificação).
builder.Services.AddHostedService<OutboxPublisher>();

// Consumidores das filas de pagamento e de reembolso.
builder.Services.AddHostedService<PaymentRequestConsumer>();
builder.Services.AddHostedService<RefundRequestConsumer>();

var app = builder.Build();

app.MapDefaultEndpoints();

// Controle do consumidor. Exposto apenas em desenvolvimento e sem
// autenticação, então nunca deve ser habilitado em ambientes reais.
if (app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "Development control endpoints are enabled. They allow pausing and resuming the RabbitMQ consumer.");

    app.MapPost("/dev/consumer/pause", (ConsumerControl control) =>
    {
        control.Pause();
        return Results.Ok(new { paused = control.IsPaused });
    });

    app.MapPost("/dev/consumer/resume", (ConsumerControl control) =>
    {
        control.Resume();
        return Results.Ok(new { paused = control.IsPaused });
    });

    app.MapGet("/dev/consumer/status", (ConsumerControl control) =>
        Results.Ok(new { paused = control.IsPaused }));
}

await app.RunAsync();
