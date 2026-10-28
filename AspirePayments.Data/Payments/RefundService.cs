using System.Globalization;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AspirePayments.Data.Payments;

/// <summary>Parâmetros de solicitação de reembolso.</summary>
public sealed record CreateRefundCommand
{
    public required Guid OrderId { get; init; }

    public Guid? PaymentTransactionId { get; init; }

    public required decimal Amount { get; init; }

    public string Currency { get; init; } = PaymentConstraints.DefaultCurrency;

    public RefundScenario Scenario { get; init; } = RefundScenario.Succeed;

    public string? Reason { get; init; }

    public string? IdempotencyKey { get; init; }
}

/// <summary>Resultado da solicitação de reembolso.</summary>
public sealed record RefundResult
{
    public required Guid RefundId { get; init; }

    public required Guid OrderId { get; init; }

    public Guid? PaymentTransactionId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required decimal Amount { get; init; }

    public required RefundStatus Status { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required bool IsIdempotentReplay { get; init; }

    public required string MessageId { get; init; }
}

/// <summary>
/// Solicita o reembolso de um pagamento já capturado. Rejeita pedidos que
/// nunca foram pagos: um pagamento recusado gera compensação, não reembolso.
/// </summary>
public sealed class RefundService
{
    private readonly PaymentsDbContext _db;
    private readonly ILogger<RefundService> _logger;

    public RefundService(PaymentsDbContext db, ILogger<RefundService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RefundResult> CreateAsync(
        CreateRefundCommand request,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey.Trim();

        if (request.Amount <= 0)
        {
            throw new PaymentValidationException("Refund amount must be greater than zero.");
        }

        if (idempotencyKey.Length > PaymentConstraints.MaxIdempotencyKeyLength)
        {
            throw new PaymentValidationException(
                $"Idempotency key must be at most {PaymentConstraints.MaxIdempotencyKeyLength} characters long.");
        }

        var existing = await _db.Refunds
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            EnsureSameLogicalRefund(existing, request);

            _logger.LogInformation(
                "Duplicate refund request detected. RefundId={RefundId} OrderId={OrderId} " +
                "IdempotencyKey={IdempotencyKey} RefundStatus={RefundStatus}",
                existing.RefundId,
                existing.OrderId,
                existing.IdempotencyKey,
                existing.Status);

            return new RefundResult
            {
                RefundId = existing.RefundId,
                OrderId = existing.OrderId,
                PaymentTransactionId = existing.PaymentTransactionId,
                IdempotencyKey = existing.IdempotencyKey,
                Amount = existing.Amount,
                Status = existing.Status,
                Reason = existing.Reason,
                CreatedAt = existing.CreatedAt,
                IsIdempotentReplay = true,
                MessageId = string.Empty
            };
        }

        var order = await _db.Orders
            .Include(o => o.PaymentTransactions)
            .FirstOrDefaultAsync(o => o.OrderId == request.OrderId, cancellationToken)
            ?? throw new OrderNotFoundException(request.OrderId);

        if (order.Status is not (OrderStatus.Paid or OrderStatus.PartiallyRefunded))
        {
            throw new InvalidPaymentStateException(
                $"Order '{order.OrderId}' is in status '{order.Status}'. Only a captured payment can be refunded.");
        }

        var captured = order.PaymentTransactions
            .Where(t => t.Status == PaymentStatus.Succeeded)
            .ToList();

        if (captured.Count == 0)
        {
            throw new InvalidPaymentStateException(
                $"Order '{order.OrderId}' has no captured payment to refund.");
        }

        var paymentTransactionId = request.PaymentTransactionId
            ?? captured.OrderBy(t => t.ProcessedAt).First().PaymentTransactionId;

        if (captured.All(t => t.PaymentTransactionId != paymentTransactionId))
        {
            throw new InvalidPaymentStateException(
                $"Payment transaction '{paymentTransactionId}' is not a captured payment of order '{order.OrderId}'.");
        }

        // Um pedido nunca pode devolver mais do que foi efetivamente capturado.
        // Reembolsos recusados (Failed) não consomem o limite; os pendentes e
        // concluídos, sim, porque ainda podem virar dinheiro devolvido.
        var capturedTotal = captured.Sum(t => t.Amount);

        var alreadyRefunded = await _db.Refunds
            .Where(r => r.OrderId == order.OrderId
                        && r.PaymentTransactionId != null
                        && (r.Status == RefundStatus.Requested
                            || r.Status == RefundStatus.Processing
                            || r.Status == RefundStatus.Succeeded))
            .SumAsync(r => r.Amount, cancellationToken);

        var refundable = capturedTotal - alreadyRefunded;

        if (request.Amount > refundable)
        {
            throw new PaymentValidationException(
                $"Refund amount {request.Amount.ToString(CultureInfo.InvariantCulture)} exceeds the " +
                $"refundable amount {refundable.ToString(CultureInfo.InvariantCulture)} " +
                $"for order '{order.OrderId}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid().ToString("N");

        var refund = new Refund
        {
            RefundId = Guid.NewGuid(),
            OrderId = order.OrderId,
            PaymentTransactionId = paymentTransactionId,
            IdempotencyKey = idempotencyKey,
            Amount = request.Amount,
            Status = RefundStatus.Requested,
            Reason = request.Reason,
            CreatedAt = now
        };

        var refundRequested = new RefundRequested
        {
            RefundId = refund.RefundId,
            OrderId = refund.OrderId,
            PaymentTransactionId = refund.PaymentTransactionId,
            IdempotencyKey = idempotencyKey,
            Amount = refund.Amount,
            Currency = request.Currency,
            Scenario = request.Scenario.ToString(),
            Reason = request.Reason,
            RequestedAt = now,
            TraceId = TraceContextAccessor.TraceId
        };

        _db.Refunds.Add(refund);
        _db.OutboxMessages.Add(OutboxFactory.Create(
            PaymentTopology.RefundRequestedType,
            PaymentTopology.EventsExchange,
            PaymentTopology.RefundRequestedRoutingKey,
            refundRequested,
            messageId));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();

            var winner = await _db.Refunds
                .FirstAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

            _logger.LogInformation(
                "Duplicate refund request detected (concurrent). RefundId={RefundId} IdempotencyKey={IdempotencyKey}",
                winner.RefundId,
                winner.IdempotencyKey);

            return new RefundResult
            {
                RefundId = winner.RefundId,
                OrderId = winner.OrderId,
                PaymentTransactionId = winner.PaymentTransactionId,
                IdempotencyKey = winner.IdempotencyKey,
                Amount = winner.Amount,
                Status = winner.Status,
                Reason = winner.Reason,
                CreatedAt = winner.CreatedAt,
                IsIdempotentReplay = true,
                MessageId = string.Empty
            };
        }

        _logger.LogInformation(
            "Refund request persisted. RefundId={RefundId} OrderId={OrderId} " +
            "PaymentTransactionId={PaymentTransactionId} IdempotencyKey={IdempotencyKey} Amount={Amount} " +
            "RefundStatus={RefundStatus} MessageId={MessageId}",
            refund.RefundId,
            refund.OrderId,
            refund.PaymentTransactionId,
            refund.IdempotencyKey,
            refund.Amount,
            refund.Status,
            messageId);

        return new RefundResult
        {
            RefundId = refund.RefundId,
            OrderId = refund.OrderId,
            PaymentTransactionId = refund.PaymentTransactionId,
            IdempotencyKey = refund.IdempotencyKey,
            Amount = refund.Amount,
            Status = refund.Status,
            Reason = refund.Reason,
            CreatedAt = refund.CreatedAt,
            IsIdempotentReplay = false,
            MessageId = messageId
        };
    }

    /// <summary>
    /// Reutilizar a chave com outro pedido, valor ou transação é conflito de
    /// negócio. Compensações (PaymentTransactionId nulo) nunca são
    /// reproduzidas como se fossem um reembolso solicitado pelo cliente.
    /// </summary>
    private static void EnsureSameLogicalRefund(Refund existing, CreateRefundCommand request)
    {
        var sameOrder = existing.OrderId == request.OrderId;
        var sameAmount = existing.Amount == request.Amount;
        var samePayment = request.PaymentTransactionId is null
            || request.PaymentTransactionId == existing.PaymentTransactionId;
        var isRealRefund = existing.Status != RefundStatus.Compensated;

        if (isRealRefund && sameOrder && sameAmount && samePayment)
        {
            return;
        }

        throw new IdempotencyConflictException(
            $"Idempotency key '{existing.IdempotencyKey}' was already used for a different refund " +
            $"(existing: order={existing.OrderId}, " +
            $"amount={existing.Amount.ToString(CultureInfo.InvariantCulture)}, " +
            $"paymentTransaction={existing.PaymentTransactionId?.ToString() ?? "<none>"}, status={existing.Status}; " +
            $"requested: order={request.OrderId}, " +
            $"amount={request.Amount.ToString(CultureInfo.InvariantCulture)}, " +
            $"paymentTransaction={request.PaymentTransactionId?.ToString() ?? "<none>"}).");
    }
}
