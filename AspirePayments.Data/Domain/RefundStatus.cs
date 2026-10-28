namespace AspirePayments.Data.Domain;

/// <summary>Estado do reembolso.</summary>
public enum RefundStatus
{
    /// <summary>Reembolso solicitado, aguardando o worker.</summary>
    Requested = 0,

    /// <summary>Worker processando o reembolso.</summary>
    Processing = 1,

    /// <summary>Reembolso concluído; valor devolvido.</summary>
    Succeeded = 2,

    /// <summary>Reembolso recusado pelo gateway.</summary>
    Failed = 3,

    /// <summary>
    /// Compensação aplicada porque o pagamento nunca foi capturado. Não houve
    /// devolução de dinheiro: o pedido foi cancelado. Uma linha com este
    /// estado sempre tem <c>PaymentTransactionId</c> nulo.
    /// </summary>
    Compensated = 4
}
