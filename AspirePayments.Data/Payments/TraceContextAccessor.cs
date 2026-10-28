using System.Diagnostics;

namespace AspirePayments.Data.Payments;

/// <summary>
/// Acesso ao contexto de trace W3C corrente. Usado para gravar o
/// <c>traceparent</c> na outbox e para propagá-lo no cabeçalho da mensagem.
/// </summary>
public static class TraceContextAccessor
{
    /// <summary>Identificador do trace atual (32 hex), ou null fora de um trace.</summary>
    public static string? TraceId => Activity.Current?.TraceId.ToString();

    /// <summary>Identificador do span atual (16 hex), ou null fora de um trace.</summary>
    public static string? SpanId => Activity.Current?.SpanId.ToString();

    /// <summary>Cabeçalho W3C <c>traceparent</c> do contexto atual.</summary>
    public static string? TraceParent => Activity.Current?.Id;

    /// <summary>Cabeçalho W3C <c>tracestate</c> do contexto atual, quando presente.</summary>
    public static string? TraceState => Activity.Current?.TraceStateString;
}
