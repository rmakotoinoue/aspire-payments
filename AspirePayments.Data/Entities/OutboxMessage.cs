namespace AspirePayments.Data.Entities;

/// <summary>
/// Mensagem da transactional outbox. É gravada na mesma transação do pedido e
/// da transação de pagamento, e publicada no RabbitMQ por um processo
/// assíncrono. Isso elimina o problema de "commit no banco sem publicar"
/// (e o inverso).
/// </summary>
public class OutboxMessage
{
    public Guid OutboxMessageId { get; set; }

    /// <summary>Identificador estável propagado no cabeçalho da mensagem.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Tipo lógico da mensagem (PaymentRequested, RefundRequested...).</summary>
    public string MessageType { get; set; } = string.Empty;

    public string Exchange { get; set; } = string.Empty;

    public string RoutingKey { get; set; } = string.Empty;

    /// <summary>Corpo da mensagem em JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Cabeçalhos em JSON (inclui o contexto de trace propagado).</summary>
    public string Headers { get; set; } = "{}";

    /// <summary>W3C traceparent do contexto que originou a mensagem.</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate, quando presente.</summary>
    public string? TraceState { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Preenchido quando a mensagem é publicada com sucesso no broker.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public int PublishAttempts { get; set; }

    public string? LastError { get; set; }
}
