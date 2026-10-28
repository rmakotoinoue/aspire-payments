using AspirePayments.Data.Domain;

namespace AspirePayments.Data.Entities;

/// <summary>
/// Tentativa de pagamento de um pedido. A <see cref="IdempotencyKey"/> é
/// única no banco: é a garantia de que uma mesma chave lógica nunca vira
/// duas transações.
/// </summary>
public class PaymentTransaction
{
    public Guid PaymentTransactionId { get; set; }

    public Guid OrderId { get; set; }

    /// <summary>Chave de idempotência. Restrição de unicidade no PostgreSQL.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "BRL";

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public string PaymentMethod { get; set; } = "SimulatedCard";

    /// <summary>Cenário determinístico que o worker deve reproduzir.</summary>
    public PaymentScenario Scenario { get; set; } = PaymentScenario.Succeed;

    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Token de concorrência: evita transições de estado duplicadas.</summary>
    public int Version { get; set; }

    public Order? Order { get; set; }

    public List<PaymentProcessingRecord> ProcessingRecords { get; set; } = [];

    public List<Refund> Refunds { get; set; } = [];
}
