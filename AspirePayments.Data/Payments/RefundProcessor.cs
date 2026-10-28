using AspirePayments.Data.Contracts;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AspirePayments.Data.Payments;

/// <summary>Desfecho do processamento de uma solicitação de reembolso.</summary>
public enum RefundProcessingOutcome
{
    Succeeded = 0,
    Failed = 1,
    Duplicate = 2,
    Discarded = 3
}

/// <summary>
/// Processa uma mensagem <see cref="RefundRequested"/>. Um reembolso só
/// acontece para um pagamento capturado: pedidos recusados geram
/// compensação, nunca reembolso.
/// </summary>
public sealed class RefundProcessor
{
    private readonly PaymentsDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly ILogger<RefundProcessor> _logger;

    public RefundProcessor(
        PaymentsDbContext db,
        IPaymentGateway gateway,
        ILogger<RefundProcessor> logger)
    {
        _db = db;
        _gateway = gateway;
        _logger = logger;
    }

    public async Task<RefundProcessingOutcome> ProcessAsync(
        RefundRequested request,
        string messageId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var refund = await _db.Refunds
            .FirstOrDefaultAsync(r => r.RefundId == request.RefundId, cancellationToken);

        if (refund is null)
        {
            _logger.LogWarning(
                "Refund message discarded: refund not found. RefundId={RefundId} MessageId={MessageId}",
                request.RefundId,
                messageId);

            return RefundProcessingOutcome.Discarded;
        }

        if (refund.Status is RefundStatus.Succeeded or RefundStatus.Failed)
        {
            _logger.LogInformation(
                "Duplicate refund request detected. RefundId={RefundId} OrderId={OrderId} " +
                "IdempotencyKey={IdempotencyKey} RefundStatus={RefundStatus} MessageId={MessageId}",
                refund.RefundId,
                refund.OrderId,
                refund.IdempotencyKey,
                refund.Status,
                messageId);

            return RefundProcessingOutcome.Duplicate;
        }

        await using var dbTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await _db.Refunds
            .Where(r => r.RefundId == request.RefundId && r.Status == RefundStatus.Requested)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.Status, RefundStatus.Processing),
                cancellationToken);

        if (claimed == 0)
        {
            await dbTransaction.RollbackAsync(cancellationToken);

            _logger.LogInformation(
                "Duplicate refund request detected (already claimed). RefundId={RefundId} MessageId={MessageId}",
                request.RefundId,
                messageId);

            return RefundProcessingOutcome.Duplicate;
        }

        _db.ChangeTracker.Clear();
        refund = await _db.Refunds.FirstAsync(r => r.RefundId == request.RefundId, cancellationToken);

        _logger.LogInformation(
            "Refund processing started. RefundId={RefundId} OrderId={OrderId} " +
            "PaymentTransactionId={PaymentTransactionId} IdempotencyKey={IdempotencyKey} Amount={Amount} " +
            "RefundStatus={RefundStatus} MessageId={MessageId} ProcessingAttempt={ProcessingAttempt}",
            refund.RefundId,
            refund.OrderId,
            refund.PaymentTransactionId,
            refund.IdempotencyKey,
            refund.Amount,
            RefundStatus.Processing,
            messageId,
            attempt);

        var scenario = Enum.TryParse<RefundScenario>(request.Scenario, out var parsed)
            ? parsed
            : RefundScenario.Succeed;

        var gatewayResult = await _gateway.RefundAsync(
            scenario,
            refund.OrderId,
            refund.Amount,
            request.Currency,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var order = await _db.Orders.FirstAsync(o => o.OrderId == refund.OrderId, cancellationToken);

        if (gatewayResult.Approved)
        {
            refund.Status = RefundStatus.Succeeded;
            refund.CompletedAt = now;
            refund.Reason ??= request.Reason;

            // O status do pedido considera o total já devolvido, não apenas
            // este reembolso: dois reembolsos parciais podem, juntos, quitar
            // o pedido.
            var refundedTotal = await _db.Refunds
                .Where(r => r.OrderId == order.OrderId
                            && r.RefundId != refund.RefundId
                            && r.Status == RefundStatus.Succeeded)
                .SumAsync(r => r.Amount, cancellationToken);

            refundedTotal += refund.Amount;

            order.Status = refundedTotal >= order.TotalAmount
                ? OrderStatus.Refunded
                : OrderStatus.PartiallyRefunded;
            order.UpdatedAt = now;
        }
        else
        {
            refund.Status = RefundStatus.Failed;
            refund.CompletedAt = now;
            refund.Reason = gatewayResult.FailureReason ?? request.Reason;

            // O pedido continua pago: a devolução falhou, o dinheiro não saiu.
            order.UpdatedAt = now;
        }

        var notification = new PaymentStatusChanged
        {
            OrderId = order.OrderId,
            PaymentTransactionId = refund.PaymentTransactionId ?? Guid.Empty,
            IdempotencyKey = refund.IdempotencyKey,
            OrderStatus = order.Status.ToString(),
            PaymentStatus = refund.Status.ToString(),
            Amount = refund.Amount,
            Currency = request.Currency,
            FailureReason = refund.Reason,
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

        if (gatewayResult.Approved)
        {
            _logger.LogInformation(
                "Refund processing completed. RefundId={RefundId} OrderId={OrderId} " +
                "PaymentTransactionId={PaymentTransactionId} IdempotencyKey={IdempotencyKey} Amount={Amount} " +
                "RefundStatus={RefundStatus} OrderStatus={OrderStatus} MessageId={MessageId}",
                refund.RefundId,
                refund.OrderId,
                refund.PaymentTransactionId,
                refund.IdempotencyKey,
                refund.Amount,
                refund.Status,
                order.Status,
                messageId);
        }
        else
        {
            _logger.LogWarning(
                "Refund processing failed. RefundId={RefundId} OrderId={OrderId} " +
                "PaymentTransactionId={PaymentTransactionId} IdempotencyKey={IdempotencyKey} Amount={Amount} " +
                "RefundStatus={RefundStatus} FailureReason={FailureReason} MessageId={MessageId}",
                refund.RefundId,
                refund.OrderId,
                refund.PaymentTransactionId,
                refund.IdempotencyKey,
                refund.Amount,
                refund.Status,
                refund.Reason,
                messageId);
        }

        return gatewayResult.Approved
            ? RefundProcessingOutcome.Succeeded
            : RefundProcessingOutcome.Failed;
    }
}
