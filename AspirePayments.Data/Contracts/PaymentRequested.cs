namespace AspirePayments.Data.Contracts;

/// <summary>
/// Solicitação de pagamento publicada pela API e consumida pelo worker.
/// <see cref="IdempotencyKey"/> é a identidade lógica da operação: é ela que
/// impede cobranças duplicadas.
/// </summary>
public sealed record PaymentRequested
{
    public required Guid PaymentTransactionId { get; init; }

    public required Guid OrderId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string PaymentMethod { get; init; }

    /// <summary>Cenário determinístico que o worker deve reproduzir.</summary>
    public required string Scenario { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Trace da requisição HTTP que originou o pedido.</summary>
    public string? TraceId { get; init; }
}
