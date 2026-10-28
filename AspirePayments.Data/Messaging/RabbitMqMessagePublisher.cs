using System.Diagnostics;
using AspirePayments.Data.Contracts;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

namespace AspirePayments.Data.Messaging;

/// <summary>Fontes de atividade do fluxo de pagamentos.</summary>
public static class PaymentTelemetry
{
    /// <summary>Nome da ActivitySource usada pelo fluxo de pagamentos.</summary>
    public const string ActivitySourceName = "AspirePayments.Payments";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}

/// <summary>
/// Publica mensagens no RabbitMQ com propagação do contexto de trace W3C
/// (traceparent/tracestate) nos cabeçalhos, para que o consumidor continue
/// a mesma cadeia de trace do produtor.
/// </summary>
public sealed class RabbitMqMessagePublisher
{
    private readonly ActivitySource _activitySource = PaymentTelemetry.ActivitySource;

    /// <summary>
    /// Propagator W3C explícito. O <c>Propagators.DefaultTextMapPropagator</c>
    /// delega para o propagador hierárquico ou W3C conforme
    /// <see cref="Activity.DefaultIdFormat"/>, o que tornaria a propagação
    /// dependente de configuração global. O requisito é W3C sempre.
    /// </summary>
    public static readonly TraceContextPropagator Propagator = new();

    /// <summary>
    /// Publica uma mensagem. <paramref name="traceParent"/> e
    /// <paramref name="traceState"/> permitem retomar o contexto gravado na
    /// outbox (publicação assíncrona, fora do request original).
    /// </summary>
    public async Task PublishAsync(
        IChannel channel,
        string exchange,
        string routingKey,
        string messageType,
        string messageId,
        string payload,
        string? traceParent,
        string? traceState,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        var parentContext = ExtractContext(traceParent, traceState);

        using var activity = _activitySource.StartActivity(
            $"{messageType} publish",
            ActivityKind.Producer,
            parentContext.ActivityContext);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.name", "publish");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("messaging.message.envelope.size", payload.Length);
        activity?.SetTag("messaging.message.type", messageType);

        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MessageHeaders.MessageId] = messageId,
            [MessageHeaders.MessageType] = messageType,
            [MessageHeaders.TraceParent] = traceParent ?? activity?.Id ?? string.Empty
        };

        if (traceState is not null)
        {
            headers[MessageHeaders.TraceState] = traceState;
        }

        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                headers[key] = value;
            }
        }

        if (activity is not null)
        {
            Propagator.Inject(
                new PropagationContext(activity.Context, Baggage.Current),
                headers,
                static (carrier, key, value) => carrier[key] = value);
        }

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = messageId,
            Type = messageType,
            Headers = headers
        };

        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: System.Text.Encoding.UTF8.GetBytes(payload),
            cancellationToken: cancellationToken);
    }

    /// <summary>Reconstrói o contexto de trace a partir dos cabeçalhos salvos.</summary>
    public static PropagationContext ExtractContext(string? traceParent, string? traceState)
    {
        if (string.IsNullOrWhiteSpace(traceParent))
        {
            return default;
        }

        var carrier = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MessageHeaders.TraceParent] = traceParent
        };

        if (!string.IsNullOrWhiteSpace(traceState))
        {
            carrier[MessageHeaders.TraceState] = traceState;
        }

        return Propagator.Extract(
            default,
            carrier,
            static (source, key) => source.TryGetValue(key, out var value) && value is not null
                ? new[] { value }
                : null);
    }
}
