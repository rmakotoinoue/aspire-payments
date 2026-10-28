namespace AspirePayments.Data.Contracts;

/// <summary>
/// Notificação de mudança de status, publicada pelo worker e consumida pela
/// API para alimentar o stream SSE do frontend. É o que substitui as
/// mensagens genéricas ("API funcionando").
/// </summary>
public sealed record PaymentStatusChanged
{
    public required Guid OrderId { get; init; }

    public required Guid PaymentTransactionId { get; init; }

    public required string IdempotencyKey { get; init; }

    /// <summary>Estado do pedido (Paid, Cancelled, Refunded...).</summary>
    public required string OrderStatus { get; init; }

    /// <summary>Estado da transação de pagamento (Succeeded, Failed...).</summary>
    public required string PaymentStatus { get; init; }

    public decimal Amount { get; init; }

    public string Currency { get; init; } = "BRL";

    public string? FailureReason { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string TraceId { get; init; }
}
