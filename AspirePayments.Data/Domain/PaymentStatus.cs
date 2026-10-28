namespace AspirePayments.Data.Domain;

/// <summary>Estado da transação de pagamento.</summary>
public enum PaymentStatus
{
    /// <summary>Transação persistida, aguardando o worker processar a mensagem.</summary>
    Pending = 0,

    /// <summary>Worker retirou a mensagem e está processando.</summary>
    Processing = 1,

    /// <summary>Pagamento aprovado e capturado.</summary>
    Succeeded = 2,

    /// <summary>Pagamento recusado pelo gateway.</summary>
    Failed = 3
}
