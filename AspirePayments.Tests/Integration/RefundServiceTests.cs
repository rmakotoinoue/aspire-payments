using AspirePayments.Data;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Payments;
using AspirePayments.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AspirePayments.Tests.Integration;

/// <summary>
/// Testes do fluxo de reembolso. A regra central: só existe reembolso
/// financeiro para um pagamento capturado. Um pagamento recusado gera
/// compensação, nunca reembolso.
/// </summary>
public class RefundServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public RefundServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_refund_cannot_be_requested_for_an_order_that_was_never_paid()
    {
        await using var db = _fixture.CreateDbContext();

        var orderService = new OrderPaymentService(db, _fixture.Logger<OrderPaymentService>());
        var refundService = new RefundService(db, _fixture.Logger<RefundService>());

        var payment = await orderService.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 30.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = $"refund-unpaid-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        // O pedido ainda está PendingPayment: não há valor capturado.
        var exception = await Assert.ThrowsAsync<InvalidPaymentStateException>(() =>
            refundService.CreateAsync(
                new CreateRefundCommand
                {
                    OrderId = payment.OrderId,
                    Amount = 30.00m,
                    Currency = "BRL",
                    Scenario = RefundScenario.Succeed
                },
                CancellationToken.None));

        Assert.Contains("captured payment", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refund_of_a_paid_order_is_requested_and_creates_a_refund_record()
    {
        await using var db = _fixture.CreateDbContext();

        var orderService = new OrderPaymentService(db, _fixture.Logger<OrderPaymentService>());
        var refundService = new RefundService(db, _fixture.Logger<RefundService>());
        var processor = new PaymentProcessor(
            db,
            new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }),
            _fixture.Logger<PaymentProcessor>());

        var payment = await orderService.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 80.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = $"refund-paid-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        var transaction = await db.PaymentTransactions
            .SingleAsync(t => t.PaymentTransactionId == payment.PaymentTransactionId);

        await processor.ProcessAsync(
            new Data.Contracts.PaymentRequested
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
            Guid.NewGuid().ToString("N"),
            1,
            CancellationToken.None);

        var refund = await refundService.CreateAsync(
            new CreateRefundCommand
            {
                OrderId = payment.OrderId,
                Amount = 80.00m,
                Currency = "BRL",
                Scenario = RefundScenario.Succeed,
                IdempotencyKey = $"refund-key-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        Assert.Equal(RefundStatus.Requested, refund.Status);
        Assert.False(refund.IsIdempotentReplay);
        Assert.Equal(payment.PaymentTransactionId, refund.PaymentTransactionId);

        var stored = await db.Refunds.SingleAsync(r => r.RefundId == refund.RefundId);
        Assert.Equal(RefundStatus.Requested, stored.Status);
        Assert.NotNull(stored.PaymentTransactionId);
    }

    [Fact]
    public async Task Repeating_a_refund_request_with_the_same_idempotency_key_does_not_duplicate_it()
    {
        await using var db = _fixture.CreateDbContext();

        var orderService = new OrderPaymentService(db, _fixture.Logger<OrderPaymentService>());
        var refundService = new RefundService(db, _fixture.Logger<RefundService>());
        var processor = new PaymentProcessor(
            db,
            new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }),
            _fixture.Logger<PaymentProcessor>());

        var payment = await orderService.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 55.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = $"refund-idem-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        var transaction = await db.PaymentTransactions
            .SingleAsync(t => t.PaymentTransactionId == payment.PaymentTransactionId);

        await processor.ProcessAsync(
            new Data.Contracts.PaymentRequested
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
            Guid.NewGuid().ToString("N"),
            1,
            CancellationToken.None);

        var key = $"refund-repeat-{Guid.NewGuid():N}";

        var command = new CreateRefundCommand
        {
            OrderId = payment.OrderId,
            Amount = 55.00m,
            Currency = "BRL",
            Scenario = RefundScenario.Succeed,
            IdempotencyKey = key
        };

        var first = await refundService.CreateAsync(command, CancellationToken.None);
        var second = await refundService.CreateAsync(command, CancellationToken.None);

        Assert.False(first.IsIdempotentReplay);
        Assert.True(second.IsIdempotentReplay);
        Assert.Equal(first.RefundId, second.RefundId);

        var refunds = await db.Refunds
            .Where(r => r.IdempotencyKey == key)
            .ToListAsync();

        Assert.Single(refunds);
    }

    [Fact]
    public async Task A_refund_is_idempotent_when_processed_twice()
    {
        await using var db = _fixture.CreateDbContext();

        var orderService = new OrderPaymentService(db, _fixture.Logger<OrderPaymentService>());
        var refundService = new RefundService(db, _fixture.Logger<RefundService>());
        var processor = new PaymentProcessor(
            db,
            new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }),
            _fixture.Logger<PaymentProcessor>());
        var refundProcessor = new RefundProcessor(
            db,
            new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }),
            _fixture.Logger<RefundProcessor>());

        var payment = await orderService.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 61.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = $"refund-process-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        var transaction = await db.PaymentTransactions
            .SingleAsync(t => t.PaymentTransactionId == payment.PaymentTransactionId);

        await processor.ProcessAsync(
            new Data.Contracts.PaymentRequested
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
            Guid.NewGuid().ToString("N"),
            1,
            CancellationToken.None);

        var refund = await refundService.CreateAsync(
            new CreateRefundCommand
            {
                OrderId = payment.OrderId,
                Amount = 61.00m,
                Currency = "BRL",
                Scenario = RefundScenario.Succeed,
                IdempotencyKey = $"refund-proc-key-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        var request = new Data.Contracts.RefundRequested
        {
            RefundId = refund.RefundId,
            OrderId = refund.OrderId,
            PaymentTransactionId = refund.PaymentTransactionId,
            IdempotencyKey = refund.IdempotencyKey,
            Amount = refund.Amount,
            Currency = "BRL",
            Scenario = nameof(RefundScenario.Succeed),
            RequestedAt = refund.CreatedAt
        };

        var first = await refundProcessor.ProcessAsync(request, Guid.NewGuid().ToString("N"), 1, CancellationToken.None);
        var second = await refundProcessor.ProcessAsync(request, Guid.NewGuid().ToString("N"), 1, CancellationToken.None);

        Assert.Equal(RefundProcessingOutcome.Succeeded, first);
        Assert.Equal(RefundProcessingOutcome.Duplicate, second);

        var order = await db.Orders.SingleAsync(o => o.OrderId == payment.OrderId);
        Assert.Equal(OrderStatus.Refunded, order.Status);

        var refunds = await db.Refunds.Where(r => r.OrderId == payment.OrderId).ToListAsync();
        Assert.Single(refunds);
        Assert.Equal(RefundStatus.Succeeded, refunds[0].Status);
    }

    [Fact]
    public async Task A_refund_for_an_unknown_order_raises_order_not_found()
    {
        await using var db = _fixture.CreateDbContext();

        var refundService = new RefundService(db, _fixture.Logger<RefundService>());
        var unknownOrderId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<OrderNotFoundException>(() =>
            refundService.CreateAsync(
                new CreateRefundCommand
                {
                    OrderId = unknownOrderId,
                    Amount = 10.00m,
                    Currency = "BRL",
                    Scenario = RefundScenario.Succeed,
                    IdempotencyKey = $"refund-unknown-{Guid.NewGuid():N}"
                },
                CancellationToken.None));

        Assert.Equal(unknownOrderId, exception.OrderId);
    }

    [Fact]
    public async Task A_refund_cannot_exceed_the_captured_amount()
    {
        await using var db = _fixture.CreateDbContext();

        var orderId = await CreatePaidOrderAsync(db, 100.00m);
        var refundService = new RefundService(db, _fixture.Logger<RefundService>());

        var exception = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            refundService.CreateAsync(
                new CreateRefundCommand
                {
                    OrderId = orderId,
                    Amount = 100.01m,
                    Currency = "BRL",
                    Scenario = RefundScenario.Succeed
                },
                CancellationToken.None));

        Assert.Contains("refundable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reusing_a_refund_key_with_a_different_amount_is_rejected()
    {
        await using var db = _fixture.CreateDbContext();

        var orderId = await CreatePaidOrderAsync(db, 100.00m);
        var refundService = new RefundService(db, _fixture.Logger<RefundService>());
        var key = $"refund-conflict-{Guid.NewGuid():N}";

        await refundService.CreateAsync(
            new CreateRefundCommand
            {
                OrderId = orderId,
                Amount = 10.00m,
                Currency = "BRL",
                Scenario = RefundScenario.Succeed,
                IdempotencyKey = key
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            refundService.CreateAsync(
                new CreateRefundCommand
                {
                    OrderId = orderId,
                    Amount = 20.00m,
                    Currency = "BRL",
                    Scenario = RefundScenario.Succeed,
                    IdempotencyKey = key
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Two_partial_refunds_that_sum_to_the_captured_amount_fully_refund_the_order()
    {
        await using var db = _fixture.CreateDbContext();

        var orderId = await CreatePaidOrderAsync(db, 100.00m);

        var refundService = new RefundService(db, _fixture.Logger<RefundService>());
        var refundProcessor = new RefundProcessor(
            db,
            new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }),
            _fixture.Logger<RefundProcessor>());

        var first = await refundService.CreateAsync(
            new CreateRefundCommand
            {
                OrderId = orderId,
                Amount = 60.00m,
                Currency = "BRL",
                Scenario = RefundScenario.Succeed,
                IdempotencyKey = $"refund-first-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        await ProcessRefundAsync(refundProcessor, first);

        var second = await refundService.CreateAsync(
            new CreateRefundCommand
            {
                OrderId = orderId,
                Amount = 40.00m,
                Currency = "BRL",
                Scenario = RefundScenario.Succeed,
                IdempotencyKey = $"refund-second-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        await ProcessRefundAsync(refundProcessor, second);

        var order = await db.Orders.SingleAsync(o => o.OrderId == orderId);
        Assert.Equal(OrderStatus.Refunded, order.Status);
    }

    private async Task<Guid> CreatePaidOrderAsync(PaymentsDbContext db, decimal amount)
    {
        var orderService = new OrderPaymentService(db, _fixture.Logger<OrderPaymentService>());
        var processor = new PaymentProcessor(
            db,
            new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }),
            _fixture.Logger<PaymentProcessor>());

        var payment = await orderService.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = amount,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = PaymentScenario.Succeed,
                IdempotencyKey = $"paid-{Guid.NewGuid():N}"
            },
            CancellationToken.None);

        var transaction = await db.PaymentTransactions
            .SingleAsync(t => t.PaymentTransactionId == payment.PaymentTransactionId);

        await processor.ProcessAsync(
            new Data.Contracts.PaymentRequested
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
            Guid.NewGuid().ToString("N"),
            1,
            CancellationToken.None);

        return payment.OrderId;
    }

    private static async Task ProcessRefundAsync(RefundProcessor processor, RefundResult refund)
    {
        await processor.ProcessAsync(
            new Data.Contracts.RefundRequested
            {
                RefundId = refund.RefundId,
                OrderId = refund.OrderId,
                PaymentTransactionId = refund.PaymentTransactionId,
                IdempotencyKey = refund.IdempotencyKey,
                Amount = refund.Amount,
                Currency = "BRL",
                Scenario = nameof(RefundScenario.Succeed),
                RequestedAt = refund.CreatedAt
            },
            Guid.NewGuid().ToString("N"),
            1,
            CancellationToken.None);
    }
}
