namespace AspirePayments.Data.Contracts;

/// <summary>Nomes dos cabeçalhos usados para propagar contexto e rastreio.</summary>
public static class MessageHeaders
{
    /// <summary>Identificador estável da mensagem (logical, gerado pela API).</summary>
    public const string MessageId = "x-message-id";

    /// <summary>Tipo lógico da mensagem.</summary>
    public const string MessageType = "x-message-type";

    /// <summary>Chave de idempotência da operação.</summary>
    public const string IdempotencyKey = "x-idempotency-key";

    /// <summary>Número da tentativa atual (incrementado a cada retry).</summary>
    public const string Attempt = "x-attempt";

    /// <summary>Contexto de trace W3C. Usado pelas instrumentações padrão.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>Contexto de trace W3C estendido.</summary>
    public const string TraceState = "tracestate";
}
