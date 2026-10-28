using System.Diagnostics;
using System.Text.Json;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Messaging;
using AspirePayments.Data.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AspirePayments.Worker.Messaging;

/// <summary>
/// Consumidor RabbitMQ com confirmação manual, retentativa com atraso e
/// roteamento para a dead-letter queue quando as tentativas se esgotam.
///
/// Semântica de entrega: <b>at-least-once</b>. A mensagem é confirmada
/// somente depois que o efeito de negócio foi gravado no banco. Se o worker
/// cair entre o commit e o ack, a mensagem é redeliverida — e é por isso que
/// o processamento precisa ser idempotente.
/// </summary>
public abstract class RabbitMqConsumerBase<TMessage> : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;
    private readonly WorkerOptions _options;
    private readonly ConsumerControl _control;
    private readonly ILogger _logger;

    private static readonly TimeSpan PausePollInterval = TimeSpan.FromMilliseconds(500);

    protected RabbitMqConsumerBase(
        IServiceProvider services,
        WorkerOptions options,
        ConsumerControl control,
        ILogger logger)
    {
        _services = services;
        _options = options;
        _control = control;
        _logger = logger;
    }

    /// <summary>Fila consumida por este consumidor.</summary>
    protected abstract string QueueName { get; }

    /// <summary>Tipo lógico da mensagem.</summary>
    protected abstract string MessageType { get; }

    /// <summary>Routing key usada na fila de retentativa.</summary>
    protected abstract string RetryRoutingKey { get; }

    /// <summary>Exchange usado para publicar a tentativa de retentativa.</summary>
    protected abstract string RetryExchange { get; }

    /// <summary>Converte o corpo da mensagem no contrato de domínio.</summary>
    protected abstract TMessage? Deserialize(ReadOnlyMemory<byte> body);

    /// <summary>Executa o efeito de negócio. Deve ser idempotente.</summary>
    protected abstract Task HandleAsync(
        TMessage message,
        string messageId,
        int attempt,
        CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Consumer started. Queue={Queue} MessageType={MessageType} MaxAttempts={MaxAttempts} " +
            "Prefetch={Prefetch} ProcessingDelay={ProcessingDelay}ms",
            QueueName,
            MessageType,
            _options.MaxAttempts,
            _options.PrefetchCount,
            _options.ProcessingDelayMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_control.IsPaused)
            {
                _logger.LogInformation(
                    "Consumer paused. Messages will stay in the queue '{Queue}' until it is resumed.",
                    QueueName);

                await Task.Delay(PausePollInterval, stoppingToken);
                continue;
            }

            try
            {
                await ConsumeSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Consumer session failed on queue '{Queue}'. Retrying in 5 seconds.",
                    QueueName);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeSessionAsync(CancellationToken stoppingToken)
    {
        var connection = _services.GetRequiredService<IConnection>();

        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await PaymentTopology.DeclareAsync(channel, stoppingToken);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, args) =>
            await OnMessageAsync(channel, args, stoppingToken);

        var consumerTag = await channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Subscribed to queue '{Queue}'. ConsumerTag={ConsumerTag}", QueueName, consumerTag);

        // Aguarda enquanto o consumo está ativo; sai quando o worker é pausado.
        while (!stoppingToken.IsCancellationRequested && !_control.IsPaused)
        {
            await Task.Delay(PausePollInterval, stoppingToken);
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Cancelling subscription on queue '{Queue}'.", QueueName);

            await channel.BasicCancelAsync(consumerTag, cancellationToken: stoppingToken);
        }
    }

    private async Task OnMessageAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        CancellationToken stoppingToken)
    {
        var headers = args.BasicProperties.Headers;
        var messageId = GetHeader(headers, MessageHeaders.MessageId)
            ?? args.BasicProperties.MessageId
            ?? string.Empty;
        var attempt = ParseAttempt(GetHeader(headers, MessageHeaders.Attempt));
        var traceParent = GetHeader(headers, MessageHeaders.TraceParent);
        var traceState = GetHeader(headers, MessageHeaders.TraceState);

        // Retoma o contexto de trace do produtor para que a cadeia continue
        // no dashboard do Aspire.
        var parentContext = RabbitMqMessagePublisher.ExtractContext(traceParent, traceState);

        using var activity = PaymentTelemetry.ActivitySource.StartActivity(
            $"{MessageType} process",
            ActivityKind.Consumer,
            parentContext.ActivityContext);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.name", "process");
        activity?.SetTag("messaging.source.name", QueueName);
        activity?.SetTag("messaging.destination.name", QueueName);
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("messaging.message.type", MessageType);
        activity?.SetTag("messaging.rabbitmq.message.delivery_tag", args.DeliveryTag);
        activity?.SetTag("processing.attempt", attempt);

        try
        {
            var message = Deserialize(args.Body);

            if (message is null)
            {
                _logger.LogError(
                    "Payment message discarded: invalid payload. MessageId={MessageId} Queue={Queue}",
                    messageId,
                    QueueName);

                await channel.BasicNackAsync(
                    deliveryTag: args.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: stoppingToken);

                return;
            }

            await HandleAsync(message, messageId, attempt, stoppingToken);

            if (_options.ProcessingDelayMilliseconds > 0)
            {
                // Atraso antes do ack: a mensagem fica "unacknowledged" e a
                // seguinte fica "ready" na fila, permitindo inspecioná-la.
                await Task.Delay(_options.ProcessingDelayMilliseconds, stoppingToken);
            }

            await channel.BasicAckAsync(
                deliveryTag: args.DeliveryTag,
                multiple: false,
                cancellationToken: stoppingToken);

            _logger.LogInformation(
                "RabbitMQ message acknowledged. MessageId={MessageId} Queue={Queue} " +
                "ProcessingAttempt={ProcessingAttempt}",
                messageId,
                QueueName,
                attempt);
        }
        catch (SimulatedWorkerCrashException ex)
        {
            // Cenário D: a transação de banco já foi confirmada, mas o worker
            // falha antes do ack. A mensagem volta e o reprocessamento
            // idempotente não pode repetir efeitos.
            _logger.LogError(
                ex,
                "Worker failed after committing the database transaction and before acknowledging. " +
                "MessageId={MessageId} Queue={Queue} ProcessingAttempt={ProcessingAttempt}. " +
                "The message will be redelivered.",
                messageId,
                QueueName,
                attempt);

            await channel.BasicNackAsync(
                deliveryTag: args.DeliveryTag,
                multiple: false,
                requeue: true,
                cancellationToken: stoppingToken);
        }
        catch (TransientPaymentException ex)
        {
            await HandleTransientFailureAsync(channel, args, messageId, attempt, ex, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento: deixa a mensagem ser redeliverida.
            await channel.BasicNackAsync(
                deliveryTag: args.DeliveryTag,
                multiple: false,
                requeue: true,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Payment processing failed. MessageId={MessageId} Queue={Queue} ProcessingAttempt={ProcessingAttempt}",
                messageId,
                QueueName,
                attempt);

            await channel.BasicNackAsync(
                deliveryTag: args.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken: stoppingToken);
        }
    }

    private async Task HandleTransientFailureAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        string messageId,
        int attempt,
        TransientPaymentException exception,
        CancellationToken stoppingToken)
    {
        if (attempt < _options.MaxAttempts)
        {
            var nextAttempt = attempt + 1;

            _logger.LogWarning(
                exception,
                "Payment processing retry scheduled. MessageId={MessageId} Queue={Queue} " +
                "ProcessingAttempt={ProcessingAttempt} NextAttempt={NextAttempt} MaxAttempts={MaxAttempts}",
                messageId,
                QueueName,
                attempt,
                nextAttempt,
                _options.MaxAttempts);

            // A mensagem vai para a fila de espera (TTL) e volta para o
            // exchange principal depois do atraso.
            var headers = args.BasicProperties.Headers is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(args.BasicProperties.Headers, StringComparer.Ordinal);

            headers[MessageHeaders.Attempt] = nextAttempt.ToString();

            var properties = new BasicProperties
            {
                ContentType = args.BasicProperties.ContentType ?? "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = messageId,
                Type = MessageType,
                Headers = headers
            };

            await channel.BasicPublishAsync(
                exchange: RetryExchange,
                routingKey: RetryRoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: args.Body,
                cancellationToken: stoppingToken);

            await channel.BasicAckAsync(
                deliveryTag: args.DeliveryTag,
                multiple: false,
                cancellationToken: stoppingToken);

            return;
        }

        _logger.LogError(
            exception,
            "Payment processing retry exhausted. MessageId={MessageId} Queue={Queue} " +
            "ProcessingAttempt={ProcessingAttempt} MaxAttempts={MaxAttempts}. Routing to the dead-letter queue.",
            messageId,
            QueueName,
            attempt,
            _options.MaxAttempts);

        // requeue: false -> a fila envia para a dead-letter queue.
        await channel.BasicNackAsync(
            deliveryTag: args.DeliveryTag,
            multiple: false,
            requeue: false,
            cancellationToken: stoppingToken);
    }

    private static string? GetHeader(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null)
        {
            return null;
        }

        if (!headers.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        // No AMQP, valores de header do tipo longstr voltam como byte[].
        return value switch
        {
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => System.Text.Encoding.UTF8.GetString(memory.Span),
            _ => value.ToString()
        };
    }

    private static int ParseAttempt(string? value)
        => int.TryParse(value, out var attempt) && attempt > 0 ? attempt : 1;
}
