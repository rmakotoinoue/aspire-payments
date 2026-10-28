namespace AspirePayments.Data.Domain;

/// <summary>Resultado de cada tentativa de processamento de uma mensagem.</summary>
public enum ProcessingStatus
{
    /// <summary>Processamento iniciado.</summary>
    Processing = 0,

    /// <summary>Pagamento aprovado.</summary>
    Succeeded = 1,

    /// <summary>Pagamento recusado.</summary>
    Failed = 2,

    /// <summary>Mensagem duplicada detectada; nenhum efeito de negócio repetido.</summary>
    Duplicate = 3,

    /// <summary>Falha transitória; nova tentativa agendada.</summary>
    Retrying = 4
}
