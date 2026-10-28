using RabbitMQ.Client;

namespace AspirePayments.Data.Contracts;

/// <summary>
/// Nomes da topologia do broker. Compartilhados entre a API (produtor) e o
/// Worker (consumidor) para que as duas pontas nunca divirjam.
/// </summary>
public static class PaymentTopology
{
    /// <summary>Exchange principal (topic) de eventos de pagamento.</summary>
    public const string EventsExchange = "payments.events";

    /// <summary>Exchange de dead letter.</summary>
    public const string DeadLetterExchange = "payments.dlx";

    /// <summary>Exchange de retentativas com atraso.</summary>
    public const string RetryExchange = "payments.retry";

    /// <summary>Fila consumida pelo worker para processar pagamentos.</summary>
    public const string PaymentRequestsQueue = "payments.requests";

    /// <summary>Fila consumida pelo worker para processar reembolsos.</summary>
    public const string RefundRequestsQueue = "payments.refunds";

    /// <summary>Fila consumida pela API para notificar o frontend via SSE.</summary>
    public const string PaymentNotificationsQueue = "payments.notifications";

    /// <summary>Fila de espera antes de uma nova tentativa.</summary>
    public const string PaymentRetryQueue = "payments.requests.retry";

    /// <summary>Fila de espera antes de uma nova tentativa de reembolso.</summary>
    public const string RefundRetryQueue = "payments.refunds.retry";

    /// <summary>Fila terminal de mensagens esgotadas.</summary>
    public const string DeadLetterQueue = "payments.dead";

    // Routing keys
    public const string PaymentRequestedRoutingKey = "payment.requested";
    public const string RefundRequestedRoutingKey = "payment.refund.requested";
    public const string PaymentStatusChangedRoutingKey = "payment.status.changed";
    public const string DeadLetterRoutingKey = "payment.dead";

    // Nomes dos tipos de mensagem (usados no cabeçalho e no log).
    public const string PaymentRequestedType = "PaymentRequested";
    public const string RefundRequestedType = "RefundRequested";
    public const string PaymentStatusChangedType = "PaymentStatusChanged";

    /// <summary>
    /// Espera (TTL) das filas de retentativa. É uma constante porque é uma
    /// propriedade da topologia: API e Worker precisam declarar a mesma fila
    /// com exatamente os mesmos argumentos, ou o RabbitMQ recusa a
    /// declaração com PRECONDITION_FAILED.
    /// </summary>
    public const int RetryDelayMilliseconds = 5000;

    /// <summary>
    /// Declara exchanges, filas e bindings. É idempotente, então API e Worker
    /// podem chamar com segurança. As filas de retentativa têm TTL e voltam
    /// para o exchange principal; as filas de processamento têm dead letter
    /// apontando para a fila de mensagens mortas.
    /// </summary>
    public static async Task DeclareAsync(
        IChannel channel,
        CancellationToken cancellationToken = default)
    {
        await channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            arguments: null, passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            DeadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            arguments: null, passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            RetryExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            arguments: null, passive: false, noWait: false, cancellationToken: cancellationToken);

        // Fila terminal de mensagens que esgotaram as tentativas.
        await channel.QueueDeclareAsync(
            DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            DeadLetterQueue, DeadLetterExchange, DeadLetterRoutingKey,
            arguments: null, noWait: false, cancellationToken: cancellationToken);

        // Fila principal de solicitações de pagamento.
        await channel.QueueDeclareAsync(
            PaymentRequestsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = DeadLetterRoutingKey
            },
            passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            PaymentRequestsQueue, EventsExchange, PaymentRequestedRoutingKey,
            arguments: null, noWait: false, cancellationToken: cancellationToken);

        // Fila principal de solicitações de reembolso.
        await channel.QueueDeclareAsync(
            RefundRequestsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = DeadLetterRoutingKey
            },
            passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            RefundRequestsQueue, EventsExchange, RefundRequestedRoutingKey,
            arguments: null, noWait: false, cancellationToken: cancellationToken);

        // Fila de notificações consumida pela API para alimentar o SSE.
        await channel.QueueDeclareAsync(
            PaymentNotificationsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            PaymentNotificationsQueue, EventsExchange, PaymentStatusChangedRoutingKey,
            arguments: null, noWait: false, cancellationToken: cancellationToken);

        // Filas de retentativa: seguram a mensagem pelo TTL e depois a
        // devolvem para o exchange principal.
        await channel.QueueDeclareAsync(
            PaymentRetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = RetryDelayMilliseconds,
                ["x-dead-letter-exchange"] = EventsExchange,
                ["x-dead-letter-routing-key"] = PaymentRequestedRoutingKey
            },
            passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            PaymentRetryQueue, RetryExchange, PaymentRequestedRoutingKey,
            arguments: null, noWait: false, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            RefundRetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = RetryDelayMilliseconds,
                ["x-dead-letter-exchange"] = EventsExchange,
                ["x-dead-letter-routing-key"] = RefundRequestedRoutingKey
            },
            passive: false, noWait: false, cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            RefundRetryQueue, RetryExchange, RefundRequestedRoutingKey,
            arguments: null, noWait: false, cancellationToken: cancellationToken);
    }
}
