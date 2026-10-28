using AspirePayments.Data;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Messaging;
using AspirePayments.Data.Payments;
using AspirePayments.Tests.Infrastructure;
using AspirePayments.Worker;
using AspirePayments.Worker.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Xunit;

namespace AspirePayments.Tests.Integration;

/// <summary>
/// Testes de integração com um RabbitMQ real: verificam que a mensagem
/// publicada existe de fato na fila, que o worker a consome, e que o
/// esgotamento de tentativas roteia a mensagem para a dead-letter queue.
/// </summary>
public class RabbitMqIntegrationTests : IClassFixture<RabbitMqFixture>, IClassFixture<PostgresFixture>
{
    private readonly RabbitMqFixture _rabbit;
    private readonly PostgresFixture _postgres;

    public RabbitMqIntegrationTests(RabbitMqFixture rabbit, PostgresFixture postgres)
    {
        _rabbit = rabbit;
        _postgres = postgres;
    }

    [Fact]
    public async Task A_published_payment_request_is_observable_in_the_queue_before_consumption()
    {
        var connection = await CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await PaymentTopology.DeclareAsync(channel, CancellationToken.None);

        // Isola o teste de execuções anteriores.
        await PurgeAsync(channel, PaymentTopology.PaymentRequestsQueue);
        await PurgeAsync(channel, PaymentTopology.DeadLetterQueue);

        var request = NewPaymentRequest(PaymentScenario.Succeed, "observable-message");
        var messageId = Guid.NewGuid().ToString("N");

        await PublishAsync(channel, request, messageId, 1);

        // A mensagem existe de verdade na fila, e pode ser lida de volta.
        var received = await channel.BasicGetAsync(
            queue: PaymentTopology.PaymentRequestsQueue,
            autoAck: false);

        Assert.NotNull(received);
        Assert.Equal(messageId, received.BasicProperties.MessageId);

        var body = System.Text.Encoding.UTF8.GetString(received.Body.Span);
        Assert.Contains(request.IdempotencyKey, body, StringComparison.Ordinal);
        Assert.Equal(PaymentTopology.PaymentRequestedRoutingKey, received.RoutingKey);

        await channel.BasicAckAsync(received.DeliveryTag, false, CancellationToken.None);
        await connection.CloseAsync();
    }

    [Fact]
    public async Task Retry_exhaustion_routes_the_message_to_the_dead_letter_queue()
    {
        var connection = await CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await PaymentTopology.DeclareAsync(channel, CancellationToken.None);

        // Purga qualquer resíduo de execuções anteriores.
        await PurgeAsync(channel, PaymentTopology.PaymentRequestsQueue);
        await PurgeAsync(channel, PaymentTopology.DeadLetterQueue);

        // A transação precisa existir e estar com o cenário transitório,
        // senão o worker descarta a mensagem (transação desconhecida) em vez
        // de falhar de forma transitória.
        var (request, _) = await CreatePendingPaymentAsync("dlq-message", PaymentScenario.FailTransient);
        var messageId = Guid.NewGuid().ToString("N");

        await PublishAsync(channel, request, messageId, 1);

        // Worker com uma única tentativa: a falha transitória esgota as
        // tentativas imediatamente e a mensagem vai para a dead-letter queue.
        await WithConsumerAsync(maxAttempts: 1, async () =>
        {
            var deadMessage = await WaitForMessageAsync(
                channel,
                PaymentTopology.DeadLetterQueue,
                TimeSpan.FromSeconds(30));

            Assert.NotNull(deadMessage);
            Assert.Equal(messageId, deadMessage.BasicProperties.MessageId);

            await channel.BasicAckAsync(deadMessage.DeliveryTag, false, CancellationToken.None);
        });

        await connection.CloseAsync();
    }

    [Fact]
    public async Task A_successful_payment_is_consumed_and_acknowledged()
    {
        var connection = await CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await PaymentTopology.DeclareAsync(channel, CancellationToken.None);
        await PurgeAsync(channel, PaymentTopology.PaymentRequestsQueue);

        // A transação precisa existir no banco: é o worker que a processa.
        var (request, orderId) = await CreatePendingPaymentAsync("consumed-message");
        var messageId = Guid.NewGuid().ToString("N");

        await PublishAsync(channel, request, messageId, 1);

        await WithConsumerAsync(maxAttempts: 3, async () =>
        {
            var processed = await WaitForConditionAsync(
                async () =>
                {
                    await using var db = _postgres.CreateDbContext();
                    return await db.PaymentTransactions
                        .AnyAsync(t => t.IdempotencyKey == request.IdempotencyKey && t.Status == PaymentStatus.Succeeded);
                },
                TimeSpan.FromSeconds(30));

            Assert.True(processed, "The payment transaction was not processed by the worker.");
        });

        await using var verifyDb = _postgres.CreateDbContext();

        var transaction = await verifyDb.PaymentTransactions
            .SingleAsync(t => t.IdempotencyKey == request.IdempotencyKey);

        Assert.Equal(PaymentStatus.Succeeded, transaction.Status);

        var order = await verifyDb.Orders.SingleAsync(o => o.OrderId == orderId);
        Assert.Equal(OrderStatus.Paid, order.Status);

        await connection.CloseAsync();
    }

    /// <summary>
    /// Executa a ação com um consumidor ativo e garante que ele seja
    /// encerrado ao final. Sem o StopAsync o consumidor continua vivo e
    /// compete pelas mensagens dos testes seguintes.
    /// </summary>
    private async Task WithConsumerAsync(int maxAttempts, Func<Task> action)
    {
        var consumer = await StartConsumerAsync(maxAttempts);

        try
        {
            await action();
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }
    }

    private async Task<(PaymentRequested Request, Guid OrderId)> CreatePendingPaymentAsync(
        string prefix,
        PaymentScenario scenario = PaymentScenario.Succeed)
    {
        await using var db = _postgres.CreateDbContext();

        var service = new OrderPaymentService(db, _postgres.Logger<OrderPaymentService>());

        var result = await service.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 100m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = scenario,
                IdempotencyKey = $"{prefix}-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        var transaction = await db.PaymentTransactions
            .SingleAsync(t => t.PaymentTransactionId == result.PaymentTransactionId);

        return (
            new PaymentRequested
            {
                PaymentTransactionId = transaction.PaymentTransactionId,
                OrderId = transaction.OrderId,
                IdempotencyKey = transaction.IdempotencyKey,
                Amount = transaction.Amount,
                Currency = transaction.Currency,
                PaymentMethod = transaction.PaymentMethod,
                Scenario = transaction.Scenario.ToString(),
                RequestedAt = transaction.CreatedAt
            },
            transaction.OrderId);
    }

    private async Task<IConnection> CreateConnectionAsync()
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_rabbit.ConnectionString),
            AutomaticRecoveryEnabled = true
        };

        return await factory.CreateConnectionAsync();
    }

    private static async Task PublishAsync(
        IChannel channel,
        PaymentRequested request,
        string messageId,
        int attempt)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MessageHeaders.MessageId] = messageId,
            [MessageHeaders.MessageType] = PaymentTopology.PaymentRequestedType,
            [MessageHeaders.Attempt] = attempt.ToString()
        };

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = messageId,
            Type = PaymentTopology.PaymentRequestedType,
            Headers = headers
        };

        var body = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)));

        await channel.BasicPublishAsync(
            exchange: PaymentTopology.EventsExchange,
            routingKey: PaymentTopology.PaymentRequestedRoutingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: CancellationToken.None);
    }

    private async Task<PaymentRequestConsumer> StartConsumerAsync(int maxAttempts)
    {
        var connection = await CreateConnectionAsync();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(_postgres.ConnectionString));
        services.AddSingleton<IPaymentGateway>(_ =>
            new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }));
        services.AddScoped<PaymentProcessor>();
        services.AddSingleton<IConnection>(_ => connection);

        var provider = services.BuildServiceProvider();

        var options = new WorkerOptions
        {
            ConsumingEnabled = true,
            MaxAttempts = maxAttempts,
            PrefetchCount = 1,
            ProcessingDelayMilliseconds = 0
        };

        var consumer = new PaymentRequestConsumer(
            provider,
            options,
            new ConsumerControl(options),
            NullLogger<PaymentRequestConsumer>.Instance);

        await consumer.StartAsync(CancellationToken.None);

        return consumer;
    }

    private static async Task PurgeAsync(IChannel channel, string queue)
        => await channel.QueuePurgeAsync(queue, CancellationToken.None);

    private static async Task<BasicGetResult?> WaitForMessageAsync(
        IChannel channel,
        string queue,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var message = await channel.BasicGetAsync(queue, autoAck: false);

            if (message is not null)
            {
                return message;
            }

            await Task.Delay(200);
        }

        return null;
    }

    private async Task<bool> WaitForConditionAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return await condition();
    }

    private PaymentRequested NewPaymentRequest(PaymentScenario scenario, string idempotencyKey)
    {
        var key = $"{idempotencyKey}-{Guid.NewGuid():N}";

        return new PaymentRequested
        {
            PaymentTransactionId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            IdempotencyKey = key,
            Amount = 100m,
            Currency = "BRL",
            PaymentMethod = "SimulatedCard",
            Scenario = scenario.ToString(),
            RequestedAt = DateTimeOffset.UtcNow
        };
    }
}
