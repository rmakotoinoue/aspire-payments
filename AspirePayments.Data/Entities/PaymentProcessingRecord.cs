using AspirePayments.Data.Domain;

namespace AspirePayments.Data.Entities;

/// <summary>
/// Trilha de auditoria de cada tentativa de processamento de uma mensagem.
/// Registra também as mensagens duplicadas detectadas.
/// </summary>
public class PaymentProcessingRecord
{
    public Guid ProcessingRecordId { get; set; }

    public Guid PaymentTransactionId { get; set; }

    /// <summary>Identificador estável da mensagem RabbitMQ.</summary>
    public string MessageId { get; set; } = string.Empty;

    public ProcessingStatus ProcessingStatus { get; set; }

    public int AttemptNumber { get; set; }

    public string? ErrorDetails { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public PaymentTransaction? PaymentTransaction { get; set; }
}
