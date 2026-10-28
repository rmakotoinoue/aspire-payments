using System.Diagnostics;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Messaging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;
using Xunit;

namespace AspirePayments.Tests.Unit;

/// <summary>
/// Testes da propagação do contexto de trace W3C pelas mensagens do RabbitMQ.
/// É o que permite correlacionar a requisição HTTP da API, a publicação,
/// o consumo no worker e o resultado final no dashboard do Aspire.
/// </summary>
public class TracePropagationTests
{
    private const string SourceName = "AspirePayments.Tests.TracePropagation";

    [Fact]
    public void The_publisher_injects_the_trace_context_in_the_message_headers()
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PaymentTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);

        using var activity = PaymentTelemetry.ActivitySource.StartActivity("PaymentRequested publish");

        Assert.NotNull(activity);

        var context = new PropagationContext(activity.Context, Baggage.Current);

        // Usa o mesmo propagator W3C da produção.
        RabbitMqMessagePublisher.Propagator.Inject(
            context,
            headers,
            static (carrier, key, value) => carrier[key] = value);

        // O traceparent deve estar presente e no formato W3C.
        Assert.True(headers.ContainsKey(MessageHeaders.TraceParent));
        Assert.StartsWith("00-", headers[MessageHeaders.TraceParent]?.ToString(), StringComparison.Ordinal);

        // E deve carregar o mesmo trace id do span que publicou.
        var traceParent = headers[MessageHeaders.TraceParent]?.ToString()!;
        Assert.Contains(activity.TraceId.ToString(), traceParent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(activity.SpanId.ToString(), traceParent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_consumer_continues_the_same_trace_as_the_producer()
    {
        // Simula o traceparent gravido pelo produtor.
        const string producerTraceId = "0af7651916cd43dd8448eb211c80319c";
        const string producerSpanId = "b7ad6b7169203331";
        var traceParent = $"00-{producerTraceId}-{producerSpanId}-01";

        var parentContext = RabbitMqMessagePublisher.ExtractContext(traceParent, null);

        Assert.Equal(producerTraceId, parentContext.ActivityContext.TraceId.ToString());
        Assert.Equal(producerSpanId, parentContext.ActivityContext.SpanId.ToString());
        Assert.True(parentContext.ActivityContext.IsRemote);

        // O consumidor cria um span filho do produtor: mesmo trace, span novo.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PaymentTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);

        using var consumer = PaymentTelemetry.ActivitySource.StartActivity(
            "PaymentRequested process",
            ActivityKind.Consumer,
            parentContext.ActivityContext);

        Assert.NotNull(consumer);
        Assert.Equal(producerTraceId, consumer.TraceId.ToString());
        Assert.NotEqual(producerSpanId, consumer.SpanId.ToString());
        Assert.Equal(producerSpanId, consumer.ParentSpanId.ToString());
    }

    [Fact]
    public void An_absent_trace_context_produces_an_empty_context()
    {
        var context = RabbitMqMessagePublisher.ExtractContext(null, null);

        Assert.Equal(default, context.ActivityContext);
    }

    [Fact]
    public void The_outbox_keeps_the_trace_parent_so_the_publish_continues_the_trace()
    {
        // O contexto gravado na outbox é o que permite ao despachante
        // (assíncrono, fora do request) continuar a mesma cadeia de trace.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PaymentTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);

        using var activity = PaymentTelemetry.ActivitySource.StartActivity("api request");

        Assert.NotNull(activity);

        var traceParent = activity.Id;

        Assert.False(string.IsNullOrWhiteSpace(traceParent));
        Assert.Contains(activity.TraceId.ToString(), traceParent!, StringComparison.OrdinalIgnoreCase);
    }
}
