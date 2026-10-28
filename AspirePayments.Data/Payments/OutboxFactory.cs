using System.Text.Json;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Entities;

namespace AspirePayments.Data.Payments;

/// <summary>
/// Constrói mensagens da outbox. A mensagem é persistida na mesma transação
/// do pedido/pagamento, e publicada depois por um processo assíncrono.
/// </summary>
public static class OutboxFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static OutboxMessage Create<TPayload>(
        string messageType,
        string exchange,
        string routingKey,
        TPayload payload,
        string messageId)
    {
        var now = DateTimeOffset.UtcNow;

        return new OutboxMessage
        {
            OutboxMessageId = Guid.NewGuid(),
            MessageId = messageId,
            MessageType = messageType,
            Exchange = exchange,
            RoutingKey = routingKey,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            Headers = JsonSerializer.Serialize(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [MessageHeaders.MessageId] = messageId,
                    [MessageHeaders.MessageType] = messageType
                },
                JsonOptions),
            TraceParent = TraceContextAccessor.TraceParent,
            TraceState = TraceContextAccessor.TraceState,
            CreatedAt = now
        };
    }
}
