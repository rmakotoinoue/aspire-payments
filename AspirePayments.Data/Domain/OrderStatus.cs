namespace AspirePayments.Data.Domain;

/// <summary>Ciclo de vida do pedido.</summary>
public enum OrderStatus
{
    /// <summary>Pedido criado, aguardando pagamento.</summary>
    PendingPayment = 0,

    /// <summary>Pagamento aprovado.</summary>
    Paid = 1,

    /// <summary>Pagamento recusado; pedido cancelado e compensação registrada.</summary>
    Cancelled = 2,

    /// <summary>Pedido reembolsado no total.</summary>
    Refunded = 3,

    /// <summary>Pedido reembolsado parcialmente.</summary>
    PartiallyRefunded = 4
}
