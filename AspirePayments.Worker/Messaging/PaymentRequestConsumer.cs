using System.Text.Json;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Payments;

namespace AspirePayments.Worker.Messaging;

/// <summary>Consumidor das solicitações de pagamento.</summary>
public sealed class PaymentRequestConsumer : RabbitMqConsumerBase<PaymentRequested>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;

    public PaymentRequestConsumer(
        IServiceProvider services,
        WorkerOptions options,
        ConsumerControl control,
        ILogger<PaymentRequestConsumer> logger)
        : base(services, options, control, logger)
    {
        _services = services;
    }

    protected override string QueueName => PaymentTopology.PaymentRequestsQueue;

    protected override string MessageType => PaymentTopology.PaymentRequestedType;

    protected override string RetryRoutingKey => PaymentTopology.PaymentRequestedRoutingKey;

    protected override string RetryExchange => PaymentTopology.RetryExchange;

    protected override PaymentRequested? Deserialize(ReadOnlyMemory<byte> body)
        => JsonSerializer.Deserialize<PaymentRequested>(body.Span, JsonOptions);

    protected override async Task HandleAsync(
        PaymentRequested message,
        string messageId,
        int attempt,
        CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();

        var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();

        await processor.ProcessAsync(message, messageId, attempt, cancellationToken);
    }
}
