namespace AspirePayments.Data.Contracts;

/// <summary>Solicitação de reembolso de um pagamento já capturado.</summary>
public sealed record RefundRequested
{
    public required Guid RefundId { get; init; }

    public required Guid OrderId { get; init; }

    public Guid? PaymentTransactionId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string Scenario { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public string? TraceId { get; init; }
}
