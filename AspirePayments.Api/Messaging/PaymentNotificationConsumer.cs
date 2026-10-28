using System.Diagnostics;
using System.Text.Json;
using AspirePayments.Api.Messaging;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Messaging;
using AspirePayments.Data.Payments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AspirePayments.Api.Messaging;

/// <summary>
/// Consome as notificações de status de pagamento publicadas pelo worker e
/// as encaminha para o hub SSE, que entrega ao frontend.
/// </summary>
public sealed class PaymentNotificationConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;
    private readonly EventChannel _hub;
    private readonly ILogger<PaymentNotificationConsumer> _logger;

    public PaymentNotificationConsumer(
        IServiceProvider services,
        EventChannel hub,
        ILogger<PaymentNotificationConsumer> logger)
    {
        _services = services;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment notification consumer failed. Retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var connection = _services.GetRequiredService<IConnection>();

        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await PaymentTopology.DeclareAsync(channel, stoppingToken);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 20,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, args) =>
        {
            var messageId = args.BasicProperties.Headers is not null
                && args.BasicProperties.Headers.TryGetValue(MessageHeaders.MessageId, out var value)
                    ? DecodeHeader(value)
                    : args.BasicProperties.MessageId;

            var traceParent = args.BasicProperties.Headers is not null
                && args.BasicProperties.Headers.TryGetValue(MessageHeaders.TraceParent, out var trace)
                    ? DecodeHeader(trace)
                    : null;

            var traceState = args.BasicProperties.Headers is not null
                && args.BasicProperties.Headers.TryGetValue(MessageHeaders.TraceState, out var state)
                    ? DecodeHeader(state)
                    : null;

            var parentContext = RabbitMqMessagePublisher.ExtractContext(traceParent, traceState);

            using var activity = PaymentTelemetry.ActivitySource.StartActivity(
                "PaymentStatusChanged notify",
                ActivityKind.Consumer,
                parentContext.ActivityContext);

            try
            {
                var notification = JsonSerializer.Deserialize<PaymentStatusChanged>(args.Body.Span, JsonOptions);

                if (notification is not null)
                {
                    _hub.Publish(notification);

                    _logger.LogInformation(
                        "Payment status notification delivered to SSE clients. OrderId={OrderId} " +
                        "PaymentTransactionId={PaymentTransactionId} IdempotencyKey={IdempotencyKey} " +
                        "PaymentStatus={PaymentStatus} OrderStatus={OrderStatus} MessageId={MessageId}",
                        notification.OrderId,
                        notification.PaymentTransactionId,
                        notification.IdempotencyKey,
                        notification.PaymentStatus,
                        notification.OrderStatus,
                        messageId);
                }

                await channel.BasicAckAsync(
                    deliveryTag: args.DeliveryTag,
                    multiple: false,
                    cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to forward the payment status notification. MessageId={MessageId}", messageId);

                await channel.BasicNackAsync(
                    deliveryTag: args.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(
            queue: PaymentTopology.PaymentNotificationsQueue,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Consuming payment status notifications from queue '{Queue}'.",
            PaymentTopology.PaymentNotificationsQueue);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    /// <summary>
    /// Valores de header do tipo longstr chegam como byte[] no AMQP.
    /// </summary>
    private static string? DecodeHeader(object? value) => value switch
    {
        null => null,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        ReadOnlyMemory<byte> memory => System.Text.Encoding.UTF8.GetString(memory.Span),
        _ => value.ToString()
    };
}
