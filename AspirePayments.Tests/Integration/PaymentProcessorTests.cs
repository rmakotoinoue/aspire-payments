using AspirePayments.Data;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Payments;
using AspirePayments.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AspirePayments.Tests.Integration;

/// <summary>
/// Testes de integração do processamento de pagamento contra um PostgreSQL
/// real. Validam as garantias que dependem de banco: índice único de
/// idempotência, transações e transições condicionais de estado.
/// </summary>
public class PaymentProcessorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PaymentProcessorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private PaymentProcessor CreateProcessor(
        PaymentsDbContext db,
        IPaymentGateway? gateway = null)
        => new(
            db,
            gateway ?? new SimulatedPaymentGateway(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = 0 }),
            _fixture.Logger<PaymentProcessor>());

    [Fact]
    public async Task Scenario_A_successful_payment_updates_the_order_to_paid()
    {
        await using var db = _fixture.CreateDbContext();

        var (payment, _) = await CreatePendingPaymentAsync(db, PaymentScenario.Succeed, "key-a-success");
        var messageId = Guid.NewGuid().ToString("N");

        var outcome = await CreateProcessor(db).ProcessAsync(payment, messageId, 1, CancellationToken.None);

        Assert.Equal(PaymentProcessingOutcome.Succeeded, outcome);

        var transaction = await db.PaymentTransactions
            .Include(t => t.ProcessingRecords)
            .SingleAsync(t => t.PaymentTransactionId == payment.PaymentTransactionId);

        Assert.Equal(PaymentStatus.Succeeded, transaction.Status);
        Assert.NotNull(transaction.ProcessedAt);
        Assert.Null(transaction.FailureReason);

        var order = await db.Orders.SingleAsync(o => o.OrderId == payment.OrderId);
        Assert.Equal(OrderStatus.Paid, order.Status);

        var records = transaction.ProcessingRecords.ToList();
        Assert.Contains(records, r => r.ProcessingStatus == ProcessingStatus.Succeeded);
        Assert.Contains(records, r => r.ProcessingStatus == ProcessingStatus.Processing);
    }

    [Fact]
    public async Task Scenario_B_failed_payment_cancels_the_order_and_records_a_compensation()
    {
        await using var db = _fixture.CreateDbContext();

        var (payment, _) = await CreatePendingPaymentAsync(db, PaymentScenario.Fail, "key-b-failure");
        var messageId = Guid.NewGuid().ToString("N");

        var outcome = await CreateProcessor(db).ProcessAsync(payment, messageId, 1, CancellationToken.None);

        Assert.Equal(PaymentProcessingOutcome.Failed, outcome);

        var transaction = await db.PaymentTransactions
            .SingleAsync(t => t.PaymentTransactionId == payment.PaymentTransactionId);

        Assert.Equal(PaymentStatus.Failed, transaction.Status);
        Assert.False(string.IsNullOrWhiteSpace(transaction.FailureReason));

        var order = await db.Orders.SingleAsync(o => o.OrderId == payment.OrderId);
        Assert.Equal(OrderStatus.Cancelled, order.Status);

        // Compensação: existe, mas NÃO é um reembolso financeiro, porque
        // nenhum valor foi capturado (PaymentTransactionId nulo).
        var compensations = await db.Refunds
            .Where(r => r.OrderId == payment.OrderId)
            .ToListAsync();

        var compensation = Assert.Single(compensations);
        Assert.Null(compensation.PaymentTransactionId);
        Assert.Equal(RefundStatus.Compensated, compensation.Status);
        Assert.Equal($"{transaction.IdempotencyKey}:compensation", compensation.IdempotencyKey);

        // Nenhum reembolso financeiro deve existir para um pagamento recusado.
        Assert.DoesNotContain(await db.Refunds.ToListAsync(), r => r.PaymentTransactionId != null);
    }

    [Fact]
    public async Task Scenario_C_duplicate_message_does_not_repeat_the_business_effects()
    {
        await using var db = _fixture.CreateDbContext();

        var (payment, _) = await CreatePendingPaymentAsync(db, PaymentScenario.Succeed, "key-c-duplicate");
        var messageId = Guid.NewGuid().ToString("N");

        var processor = CreateProcessor(db);

        var first = await processor.ProcessAsync(payment, messageId, 1, CancellationToken.None);
        var second = await processor.ProcessAsync(payment, messageId, 1, CancellationToken.None);

        Assert.Equal(PaymentProcessingOutcome.Succeeded, first);
        Assert.Equal(PaymentProcessingOutcome.Duplicate, second);

        var transactionCount = await db.PaymentTransactions
            .CountAsync(t => t.OrderId == payment.OrderId);

        Assert.Equal(1, transactionCount);

        var succeededRecords = await db.PaymentProcessingRecords
            .CountAsync(r => r.PaymentTransactionId == payment.PaymentTransactionId
                             && r.ProcessingStatus == ProcessingStatus.Succeeded);

        Assert.Equal(1, succeededRecords);

        var order = await db.Orders.SingleAsync(o => o.OrderId == payment.OrderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
    }

    [Fact]
    public async Task Scenario_D_redelivery_after_a_committed_transaction_does_not_duplicate_the_payment()
    {
        await using var db = _fixture.CreateDbContext();

        var (payment, _) = await CreatePendingPaymentAsync(db, PaymentScenario.Succeed, "key-d-redelivery");
        var messageId = Guid.NewGuid().ToString("N");

        var processor = CreateProcessor(db);

        // Primeira entrega: sucesso.
        await processor.ProcessAsync(payment, messageId, 1, CancellationToken.None);

        // Redelivery da MESMA mensagem após o commit: não pode repetir efeitos.
        var redelivery = await processor.ProcessAsync(payment, messageId, 1, CancellationToken.None);

        Assert.Equal(PaymentProcessingOutcome.Duplicate, redelivery);

        var succeededRecords = await db.PaymentProcessingRecords
            .CountAsync(r => r.PaymentTransactionId == payment.PaymentTransactionId
                             && r.ProcessingStatus == ProcessingStatus.Succeeded);

        Assert.Equal(1, succeededRecords);

        var transactionCount = await db.PaymentTransactions
            .CountAsync(t => t.OrderId == payment.OrderId);

        Assert.Equal(1, transactionCount);
    }

    [Fact]
    public async Task Scenario_D_redelivery_with_a_different_message_id_is_also_detected_as_duplicate()
    {
        await using var db = _fixture.CreateDbContext();

        var (payment, _) = await CreatePendingPaymentAsync(db, PaymentScenario.Succeed, "key-d-new-message");

        var processor = CreateProcessor(db);

        await processor.ProcessAsync(payment, Guid.NewGuid().ToString("N"), 1, CancellationToken.None);

        // Outra cópia da mesma solicitação lógica (chave de idempotência igual).
        var duplicate = await processor.ProcessAsync(payment, Guid.NewGuid().ToString("N"), 1, CancellationToken.None);

        Assert.Equal(PaymentProcessingOutcome.Duplicate, duplicate);

        var succeededRecords = await db.PaymentProcessingRecords
            .CountAsync(r => r.PaymentTransactionId == payment.PaymentTransactionId
                             && r.ProcessingStatus == ProcessingStatus.Succeeded);

        Assert.Equal(1, succeededRecords);
    }

    [Fact]
    public async Task Scenario_A_concurrent_workers_cannot_finalize_the_same_transaction_twice()
    {
        await using var setupDb = _fixture.CreateDbContext();

        var (payment, _) = await CreatePendingPaymentAsync(setupDb, PaymentScenario.Succeed, "key-concurrent");

        // Dois processadores independentes, cada um com seu DbContext.
        await using var db1 = _fixture.CreateDbContext();
        await using var db2 = _fixture.CreateDbContext();

        var processor1 = CreateProcessor(db1);
        var processor2 = CreateProcessor(db2);

        var results = await Task.WhenAll(
            processor1.ProcessAsync(payment, Guid.NewGuid().ToString("N"), 1, CancellationToken.None),
            processor2.ProcessAsync(payment, Guid.NewGuid().ToString("N"), 1, CancellationToken.None));

        Assert.Contains(PaymentProcessingOutcome.Succeeded, results);
        Assert.Contains(PaymentProcessingOutcome.Duplicate, results);

        await using var verifyDb = _fixture.CreateDbContext();

        var succeededRecords = await verifyDb.PaymentProcessingRecords
            .CountAsync(r => r.PaymentTransactionId == payment.PaymentTransactionId
                             && r.ProcessingStatus == ProcessingStatus.Succeeded);

        Assert.Equal(1, succeededRecords);
    }

    [Fact]
    public async Task Scenario_D_fail_after_commit_commits_once_and_is_a_duplicate_on_redelivery()
    {
        await using var db = _fixture.CreateDbContext();

        var (payment, _) = await CreatePendingPaymentAsync(db, PaymentScenario.FailAfterCommit, "key-fail-after-commit");
        var messageId = Guid.NewGuid().ToString("N");

        var processor = CreateProcessor(db);

        // O worker confirma a transação e então "cai" antes do ack.
        await Assert.ThrowsAsync<SimulatedWorkerCrashException>(() =>
            processor.ProcessAsync(payment, messageId, 1, CancellationToken.None));

        await using var verifyDb = _fixture.CreateDbContext();

        // O efeito de negócio já estava confirmado no banco antes da falha.
        var transaction = await verifyDb.PaymentTransactions
            .SingleAsync(t => t.PaymentTransactionId == payment.PaymentTransactionId);

        Assert.Equal(PaymentStatus.Succeeded, transaction.Status);

        var order = await verifyDb.Orders.SingleAsync(o => o.OrderId == payment.OrderId);
        Assert.Equal(OrderStatus.Paid, order.Status);

        // A redelivery da mesma mensagem não pode repetir os efeitos.
        var redelivery = await processor.ProcessAsync(payment, messageId, 2, CancellationToken.None);

        Assert.Equal(PaymentProcessingOutcome.Duplicate, redelivery);

        var succeededRecords = await verifyDb.PaymentProcessingRecords
            .CountAsync(r => r.PaymentTransactionId == payment.PaymentTransactionId
                             && r.ProcessingStatus == ProcessingStatus.Succeeded);

        Assert.Equal(1, succeededRecords);
    }

    [Fact]
    public async Task A_message_for_an_unknown_transaction_is_discarded()
    {
        await using var db = _fixture.CreateDbContext();

        var request = new PaymentRequested
        {
            PaymentTransactionId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            IdempotencyKey = "unknown",
            Amount = 10m,
            Currency = "BRL",
            PaymentMethod = "SimulatedCard",
            Scenario = nameof(PaymentScenario.Succeed),
            RequestedAt = DateTimeOffset.UtcNow
        };

        var outcome = await CreateProcessor(db)
            .ProcessAsync(request, Guid.NewGuid().ToString("N"), 1, CancellationToken.None);

        Assert.Equal(PaymentProcessingOutcome.Discarded, outcome);
    }

    private async Task<(PaymentRequested Payment, Guid OrderId)> CreatePendingPaymentAsync(
        PaymentsDbContext db,
        PaymentScenario scenario,
        string idempotencyKey)
    {
        var service = new OrderPaymentService(db, _fixture.Logger<OrderPaymentService>());

        var result = await service.CreateAsync(
            new CreateOrderPaymentRequest
            {
                TotalAmount = 250.00m,
                Currency = "BRL",
                PaymentMethod = "SimulatedCard",
                Scenario = scenario,
                IdempotencyKey = idempotencyKey
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
}
