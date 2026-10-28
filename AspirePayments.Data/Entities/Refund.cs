using AspirePayments.Data.Domain;

namespace AspirePayments.Data.Entities;

/// <summary>
/// Reembolso de um pagamento já capturado. Nunca representa um pagamento
/// recusado: para o cenário de falha existe a tabela de compensação
/// (<see cref="PaymentTransaction.FailureReason"/>) e a ordem é cancelada.
/// </summary>
public class Refund
{
    public Guid RefundId { get; set; }

    public Guid OrderId { get; set; }

    /// <summary>Nulo apenas quando não houve pagamento capturado.</summary>
    public Guid? PaymentTransactionId { get; set; }

    /// <summary>Garante que o mesmo pedido de reembolso não seja executado duas vezes.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public RefundStatus Status { get; set; } = RefundStatus.Requested;

    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Order? Order { get; set; }

    public PaymentTransaction? PaymentTransaction { get; set; }
}
