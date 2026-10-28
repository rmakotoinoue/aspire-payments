using AspirePayments.Data;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Payments;
using AspirePayments.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AspirePayments.Tests.Integration;

/// <summary>
/// Testes de idempotência na criação do pedido e do pagamento. O índice
/// único do PostgreSQL é a garantia real: o código apenas detecta a
/// violação e devolve o resultado existente.
/// </summary>
public class OrderPaymentServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public OrderPaymentServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private OrderPaymentService CreateService(PaymentsDbContext db)
        => new(db, _fixture.Logger<OrderPaymentService>());

    [Fact]
    public async Task Creating_a_payment_for_an_unknown_order_raises_order_not_found()
    {
        await using var db = _fixture.CreateDbContext();

        var service = CreateService(db);
        var unknownOrderId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<OrderNotFoundException>(() =>
            service.CreateAsync(
                new CreateOrderPaymentRequest
                {
                    OrderId = unknownOrderId,
                    TotalAmount = 10.00m,
                    Currency = "BRL",
                    PaymentMethod = "SimulatedCard",
                    Scenario = PaymentScenario.Succeed,
                    IdempotencyKey = $"unknown-order-{Guid.NewGuid():N}"
                },
                CancellationToken.None));

        // A API traduz esta exceção em 404; um pedido em estado inválido
        // continua sendo 409.
        Assert.Equal(unknownOrderId, exception.OrderId);
    }

    [Fact]
    public async Task Repeated_requests_with_the_same_idempotency_key_do_not_create_duplicate_transactions()
    {
        await using var db = _fixture.CreateDbContext();

        var service = CreateService(db);
        var key = $"key-repeat-{Guid.NewGuid():N}";

        var request = new CreateOrderPaymentRequest
        {
            TotalAmount = 99.90m,
            Currency = "BRL",
            PaymentMethod = "SimulatedCard",
            Scenario = PaymentScenario.Succeed,
            IdempotencyKey = key
        };

        var first = await service.CreateAsync(request, CancellationToken.None);
        var second = await service.CreateAsync(request, CancellationToken.None);

        Assert.False(first.IsIdempotentReplay);
        Assert.True(second.IsIdempotentReplay);
        Assert.Equal(first.PaymentTransactionId, second.PaymentTransactionId);
        Assert.Equal(first.OrderId, second.OrderId);

        var transactions = await db.PaymentTransactions
            .Where(t => t.IdempotencyKey == key)
            .ToListAsync();

        Assert.Single(transactions);

        // Também não pode haver um pedido duplicado para esta chave. A
        // contagem é escopada pela chave porque o banco é compartilhado
        // entre os testes da classe.
        var ordersForKey = await db.Orders
            .CountAsync(o => o.PaymentTransactions.Any(t => t.IdempotencyKey == key));

        Assert.Equal(1, ordersForKey);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_idempotency_key_create_a_single_transaction()
    {
        var key = $"key-concurrent-{Guid.NewGuid():N}";

        // Cada requisição usa seu próprio DbContext, como aconteceria com
        // requisições HTTP simultâneas.
        await using var db1 = _fixture.CreateDbContext();
        await using var db2 = _fixture.CreateDbContext();

        var service1 = CreateService(db1);
        var service2 = CreateService(db2);

        var request = new CreateOrderPaymentRequest
        {
            TotalAmount = 150.00m,
            Currency = "BRL",
            PaymentMethod = "SimulatedPix",
            Scenario = PaymentScenario.Succeed,
            IdempotencyKey = key
        };

        var results = await Task.WhenAll(
            service1.CreateAsync(request, CancellationToken.None),
            service2.CreateAsync(request, CancellationToken.None));

        Assert.Equal(results[0].PaymentTransactionId, results[1].PaymentTransactionId);
        Assert.Contains(results, r => r.IsIdempotentReplay);

        await using var verifyDb = _fixture.CreateDbContext();

        var transactions = await verifyDb.PaymentTransactions
            .Where(t => t.IdempotencyKey == key)
            .ToListAsync();

        Assert.Single(transactions);

        var ordersForKey = await verifyDb.Orders
            .CountAsync(o => o.PaymentTransactions.Any(t => t.IdempotencyKey == key));

        Assert.Equal(1, ordersForKey);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_amount_is_rejected()
    {
        await using var db = _fixture.CreateDbContext();

        var service = CreateService(db);
        var key = $"key-conflict-{Guid.NewGuid():N}";

        await service.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 10.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = key
            },
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            service.CreateAsync(
                new CreateOrderPaymentRequest
                {
                    TotalAmount = 999.00m,
                    Currency = "BRL",
                    PaymentMethod = "SimulatedCard",
                    Scenario = PaymentScenario.Succeed,
                    IdempotencyKey = key
                },
                CancellationToken.None));

        Assert.Contains("different payment", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_payment_method_is_rejected()
    {
        await using var db = _fixture.CreateDbContext();

        var service = CreateService(db);
        var key = $"key-conflict-method-{Guid.NewGuid():N}";

        await service.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 10.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = key
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            service.CreateAsync(
                new CreateOrderPaymentRequest
                {
                    TotalAmount = 10.00m,
                    Currency = "BRL",
                    PaymentMethod = "SimulatedBoleto",
                    Scenario = PaymentScenario.Succeed,
                    IdempotencyKey = key
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task A_generated_idempotency_key_is_used_when_none_is_supplied()
    {
        await using var db = _fixture.CreateDbContext();

        var result = await CreateService(db).CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 42.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed
            },
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.IdempotencyKey));
        Assert.False(result.IsIdempotentReplay);
    }

    [Fact]
    public async Task The_outbox_message_is_written_in_the_same_transaction_as_the_order()
    {
        await using var db = _fixture.CreateDbContext();

        var result = await CreateService(db).CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 77.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = $"key-outbox-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        var outbox = await db.OutboxMessages
            .SingleAsync(m => m.MessageId == result.MessageId);

        Assert.Equal("PaymentRequested", outbox.MessageType);
        Assert.Equal(result.MessageId, outbox.MessageId);
        Assert.Equal("payments.events", outbox.Exchange);
        Assert.Equal("payment.requested", outbox.RoutingKey);
        Assert.Null(outbox.PublishedAt);
        Assert.Contains(result.PaymentTransactionId.ToString(), outbox.Payload);
    }
}
