using AspirePayments.Data.Domain;

namespace AspirePayments.Data.Entities;

/// <summary>Pedido do cliente. Um pedido tem no mínimo uma transação de pagamento.</summary>
public class Order
{
    public Guid OrderId { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.PendingPayment;

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = "BRL";

    public string PaymentMethod { get; set; } = "SimulatedCard";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Trace da requisição que originou o pedido, para correlação.</summary>
    public string? TraceId { get; set; }

    public List<PaymentTransaction> PaymentTransactions { get; set; } = [];

    public List<Refund> Refunds { get; set; } = [];
}
