using System.Text.Json;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Payments;

namespace AspirePayments.Worker.Messaging;

/// <summary>Consumidor das solicitações de reembolso.</summary>
public sealed class RefundRequestConsumer : RabbitMqConsumerBase<RefundRequested>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;

    public RefundRequestConsumer(
        IServiceProvider services,
        WorkerOptions options,
        ConsumerControl control,
        ILogger<RefundRequestConsumer> logger)
        : base(services, options, control, logger)
    {
        _services = services;
    }

    protected override string QueueName => PaymentTopology.RefundRequestsQueue;

    protected override string MessageType => PaymentTopology.RefundRequestedType;

    protected override string RetryRoutingKey => PaymentTopology.RefundRequestedRoutingKey;

    protected override string RetryExchange => PaymentTopology.RetryExchange;

    protected override RefundRequested? Deserialize(ReadOnlyMemory<byte> body)
        => JsonSerializer.Deserialize<RefundRequested>(body.Span, JsonOptions);

    protected override async Task HandleAsync(
        RefundRequested message,
        string messageId,
        int attempt,
        CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();

        var processor = scope.ServiceProvider.GetRequiredService<RefundProcessor>();

        await processor.ProcessAsync(message, messageId, attempt, cancellationToken);
    }
}
