using AspirePayments.Data.Contracts;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AspirePayments.Data.Payments;

/// <summary>Desfecho do processamento de uma mensagem de pagamento.</summary>
public enum PaymentProcessingOutcome
{
    /// <summary>Pagamento aprovado e pedido marcado como pago.</summary>
    Succeeded = 0,

    /// <summary>Pagamento recusado e pedido cancelado com compensação.</summary>
    Failed = 1,

    /// <summary>Mensagem duplicada: nenhum efeito de negócio foi repetido.</summary>
    Duplicate = 2,

    /// <summary>Transação de pagamento inexistente: mensagem descartada.</summary>
    Discarded = 3
}

/// <summary>
/// Processa uma mensagem <see cref="PaymentRequested"/>.
///
/// Garantias implementadas aqui:
/// <list type="bullet">
/// <item>O estado da transação só avança por uma atualização condicional
/// (Pending -> Processing), então duas-workers concorrentes não finalizam a
/// mesma transação.</item>
/// <item>Se a transação já está Succeeded/Failed, a mensagem é tratada como
/// duplicata: nenhum efeito de negócio é repetido.</item>
/// <item>Pedido, transação, registros, compensação e outbox são gravados na
/// MESMA transação de banco. O ack da mensagem acontece só depois do
/// commit.</item>
/// </list>
/// </summary>
public sealed class PaymentProcessor
{
    private readonly PaymentsDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(
        PaymentsDbContext db,
        IPaymentGateway gateway,
        ILogger<PaymentProcessor> logger)
    {
        _db = db;
        _gateway = gateway;
        _logger = logger;
    }

    public async Task<PaymentProcessingOutcome> ProcessAsync(
        PaymentRequested request,
        string messageId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var transaction = await _db.PaymentTransactions
            .Include(t => t.Order)
            .FirstOrDefaultAsync(
                t => t.PaymentTransactionId == request.PaymentTransactionId,
                cancellationToken);

        if (transaction is null)
        {
            _logger.LogWarning(
                "Payment message discarded: transaction not found. " +
                "PaymentTransactionId={PaymentTransactionId} MessageId={MessageId} IdempotencyKey={IdempotencyKey}",
                request.PaymentTransactionId,
                messageId,
                request.IdempotencyKey);

            return PaymentProcessingOutcome.Discarded;
        }

        if (transaction.Status is PaymentStatus.Succeeded or PaymentStatus.Failed)
        {
            await RecordDuplicateAsync(transaction, messageId, attempt, cancellationToken);

            _logger.LogInformation(
                "Duplicate payment request detected. PaymentTransactionId={PaymentTransactionId} " +
                "IdempotencyKey={IdempotencyKey} PaymentStatus={PaymentStatus} MessageId={MessageId} " +
                "ProcessingAttempt={ProcessingAttempt}",
                transaction.PaymentTransactionId,
                transaction.IdempotencyKey,
                transaction.Status,
                messageId,
                attempt);

            return PaymentProcessingOutcome.Duplicate;
        }

        await using var dbTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Claim condicional: só a transação que está em Pending é reivindicada.
        // A linha fica bloqueada até o commit, o que serializa workers
        // concorrentes que recebam a mesma mensagem.
        var claimed = await _db.PaymentTransactions
            .Where(t => t.PaymentTransactionId == request.PaymentTransactionId
                        && t.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, PaymentStatus.Processing)
                    .SetProperty(t => t.Version, t => t.Version + 1),
                cancellationToken);

        if (claimed == 0)
        {
            // Outra worker (ou uma redelivery) já reivindicou a transação.
            await dbTransaction.RollbackAsync(cancellationToken);

            _logger.LogInformation(
                "Duplicate payment request detected (already claimed). " +
                "PaymentTransactionId={PaymentTransactionId} IdempotencyKey={IdempotencyKey} MessageId={MessageId}",
                request.PaymentTransactionId,
                request.IdempotencyKey,
                messageId);

            return PaymentProcessingOutcome.Duplicate;
        }

        _db.ChangeTracker.Clear();
        transaction = await _db.PaymentTransactions
            .Include(t => t.Order)
            .FirstAsync(t => t.PaymentTransactionId == request.PaymentTransactionId, cancellationToken);

        _db.PaymentProcessingRecords.Add(new PaymentProcessingRecord
        {
            ProcessingRecordId = Guid.NewGuid(),
            PaymentTransactionId = transaction.PaymentTransactionId,
            MessageId = messageId,
            ProcessingStatus = ProcessingStatus.Processing,
            AttemptNumber = attempt,
            CreatedAt = DateTimeOffset.UtcNow
        });

        _logger.LogInformation(
            "Payment processing started. OrderId={OrderId} PaymentTransactionId={PaymentTransactionId} " +
            "IdempotencyKey={IdempotencyKey} Amount={Amount} Currency={Currency} MessageId={MessageId} " +
            "ProcessingAttempt={ProcessingAttempt} PaymentStatus={PaymentStatus}",
            transaction.OrderId,
            transaction.PaymentTransactionId,
            transaction.IdempotencyKey,
            transaction.Amount,
            transaction.Currency,
            messageId,
            attempt,
            PaymentStatus.Processing);

        await _db.SaveChangesAsync(cancellationToken);

        // O gateway simulado é local e não faz I/O externo, então a transação
        // permanece aberta apenas durante a simulação. Com um gateway real,
        // esta chamada deve sair da transação e o próprio gateway deve
        // receber a IdempotencyKey para deduplicar.
        var scenario = Enum.TryParse<PaymentScenario>(request.Scenario, out var parsed)
            ? parsed
            : PaymentScenario.Succeed;

        var gatewayResult = await _gateway.ChargeAsync(
            scenario,
            transaction.OrderId,
            transaction.Amount,
            transaction.Currency,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var order = transaction.Order!;

        if (gatewayResult.Approved)
        {
            transaction.Status = PaymentStatus.Succeeded;
            transaction.ProcessedAt = now;
            transaction.FailureReason = null;
            order.Status = OrderStatus.Paid;
            order.UpdatedAt = now;

            _db.PaymentProcessingRecords.Add(new PaymentProcessingRecord
            {
                ProcessingRecordId = Guid.NewGuid(),
                PaymentTransactionId = transaction.PaymentTransactionId,
                MessageId = messageId,
                ProcessingStatus = ProcessingStatus.Succeeded,
                AttemptNumber = attempt,
                CreatedAt = now
            });
        }
        else
        {
            transaction.Status = PaymentStatus.Failed;
            transaction.ProcessedAt = now;
            transaction.FailureReason = gatewayResult.FailureReason;
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = now;

            _db.PaymentProcessingRecords.Add(new PaymentProcessingRecord
            {
                ProcessingRecordId = Guid.NewGuid(),
                PaymentTransactionId = transaction.PaymentTransactionId,
                MessageId = messageId,
                ProcessingStatus = ProcessingStatus.Failed,
                AttemptNumber = attempt,
                ErrorDetails = gatewayResult.FailureReason,
                CreatedAt = now
            });

            // Compensação: o pagamento nunca foi capturado, então não existe
            // reembolso financeiro. Registramos a compensação com
            // PaymentTransactionId nulo, o que a distingue de um reembolso.
            // A chave de idempotência derivada impede duplicar a compensação
            // quando a mensagem for reprocessada.
            _db.Refunds.Add(new Refund
            {
                RefundId = Guid.NewGuid(),
                OrderId = order.OrderId,
                PaymentTransactionId = null,
                IdempotencyKey = $"{transaction.IdempotencyKey}:compensation",
                Amount = transaction.Amount,
                Status = RefundStatus.Compensated,
                Reason = gatewayResult.FailureReason,
                CreatedAt = now,
                CompletedAt = now
            });
        }

        var notification = new PaymentStatusChanged
        {
            OrderId = order.OrderId,
            PaymentTransactionId = transaction.PaymentTransactionId,
            IdempotencyKey = transaction.IdempotencyKey,
            OrderStatus = order.Status.ToString(),
            PaymentStatus = transaction.Status.ToString(),
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            FailureReason = transaction.FailureReason,
            OccurredAt = now,
            TraceId = TraceContextAccessor.TraceId ?? string.Empty
        };

        _db.OutboxMessages.Add(OutboxFactory.Create(
            PaymentTopology.PaymentStatusChangedType,
            PaymentTopology.EventsExchange,
            PaymentTopology.PaymentStatusChangedRoutingKey,
            notification,
            Guid.NewGuid().ToString("N")));

        await _db.SaveChangesAsync(cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);

        if (scenario == PaymentScenario.FailAfterCommit)
        {
            // Cenário D: o efeito de negócio já está confirmado no banco, mas
            // o worker "cai" antes do ack. A mensagem volta para a fila e o
            // reprocessamento idempotente detecta a duplicata, sem repetir
            // efeitos. Sem este throw o cenário não demonstraria nada.
            throw new SimulatedWorkerCrashException(
                $"Simulated worker crash after commit for transaction '{transaction.PaymentTransactionId}'.");
        }

        if (gatewayResult.Approved)
        {
            _logger.LogInformation(
                "Payment succeeded. OrderId={OrderId} PaymentTransactionId={PaymentTransactionId} " +
                "IdempotencyKey={IdempotencyKey} Amount={Amount} Currency={Currency} PaymentStatus={PaymentStatus} " +
                "OrderStatus={OrderStatus} MessageId={MessageId} ProcessingAttempt={ProcessingAttempt}",
                order.OrderId,
                transaction.PaymentTransactionId,
                transaction.IdempotencyKey,
                transaction.Amount,
                transaction.Currency,
                transaction.Status,
                order.Status,
                messageId,
                attempt);
        }
        else
        {
            _logger.LogWarning(
                "Payment failed. OrderId={OrderId} PaymentTransactionId={PaymentTransactionId} " +
                "IdempotencyKey={IdempotencyKey} Amount={Amount} Currency={Currency} PaymentStatus={PaymentStatus} " +
                "OrderStatus={OrderStatus} FailureReason={FailureReason} MessageId={MessageId} " +
                "ProcessingAttempt={ProcessingAttempt}",
                order.OrderId,
                transaction.PaymentTransactionId,
                transaction.IdempotencyKey,
                transaction.Amount,
                transaction.Currency,
                transaction.Status,
                order.Status,
                transaction.FailureReason,
                messageId,
                attempt);
        }

        return gatewayResult.Approved
            ? PaymentProcessingOutcome.Succeeded
            : PaymentProcessingOutcome.Failed;
    }

    private async Task RecordDuplicateAsync(
        PaymentTransaction transaction,
        string messageId,
        int attempt,
        CancellationToken cancellationToken)
    {
        // Só registra a duplicata uma vez por mensagem.
        var alreadyRecorded = await _db.PaymentProcessingRecords
            .AnyAsync(
                r => r.PaymentTransactionId == transaction.PaymentTransactionId
                     && r.MessageId == messageId
                     && r.ProcessingStatus == ProcessingStatus.Duplicate,
                cancellationToken);

        if (alreadyRecorded)
        {
            return;
        }

        _db.PaymentProcessingRecords.Add(new PaymentProcessingRecord
        {
            ProcessingRecordId = Guid.NewGuid(),
            PaymentTransactionId = transaction.PaymentTransactionId,
            MessageId = messageId,
            ProcessingStatus = ProcessingStatus.Duplicate,
            AttemptNumber = attempt,
            ErrorDetails = "Message already processed; no business side effect was repeated.",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
