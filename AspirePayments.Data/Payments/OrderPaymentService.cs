using System.Globalization;
using AspirePayments.Data.Contracts;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AspirePayments.Data.Payments;

/// <summary>Parâmetros de criação de um pedido com pagamento.</summary>
public sealed record CreateOrderPaymentRequest
{
    public required decimal TotalAmount { get; init; }

    public string Currency { get; init; } = PaymentConstraints.DefaultCurrency;

    public string PaymentMethod { get; init; } = "SimulatedCard";

    public PaymentScenario Scenario { get; init; } = PaymentScenario.Succeed;

    /// <summary>Chave de idempotência. Quando ausente, é gerada pelo servidor.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Pedido existente ao qual o pagamento será vinculado.</summary>
    public Guid? OrderId { get; init; }
}

/// <summary>Resultado da criação (ou reuso) de um pagamento.</summary>
public sealed record OrderPaymentResult
{
    public required Guid OrderId { get; init; }

    public required Guid PaymentTransactionId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PaymentStatus PaymentStatus { get; init; }

    public required OrderStatus OrderStatus { get; init; }

    /// <summary>True quando a chave de idempotência já existia e nada foi refeito.</summary>
    public required bool IsIdempotentReplay { get; init; }

    public required string MessageId { get; init; }
}

/// <summary>
/// Criação de pedido + pagamento. Pedido, transação de pagamento e mensagem
/// da outbox são gravados na MESMA transação de banco: ou tudo é persistido,
/// ou nada é. A publicação no RabbitMQ acontece depois, de forma assíncrona.
/// </summary>
public sealed class OrderPaymentService
{
    private readonly PaymentsDbContext _db;
    private readonly ILogger<OrderPaymentService> _logger;

    public OrderPaymentService(PaymentsDbContext db, ILogger<OrderPaymentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<OrderPaymentResult> CreateAsync(
        CreateOrderPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey.Trim();

        if (request.TotalAmount <= 0)
        {
            throw new PaymentValidationException("Total amount must be greater than zero.");
        }

        if (idempotencyKey.Length > PaymentConstraints.MaxIdempotencyKeyLength)
        {
            throw new PaymentValidationException(
                $"Idempotency key must be at most {PaymentConstraints.MaxIdempotencyKeyLength} characters long.");
        }

        var existing = await _db.PaymentTransactions
            .Include(t => t.Order)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            EnsureSameLogicalPayment(existing, request);

            _logger.LogInformation(
                "Duplicate payment request detected. IdempotencyKey={IdempotencyKey} " +
                "PaymentTransactionId={PaymentTransactionId} PaymentStatus={PaymentStatus}",
                idempotencyKey,
                existing.PaymentTransactionId,
                existing.Status);

            return new OrderPaymentResult
            {
                OrderId = existing.OrderId,
                PaymentTransactionId = existing.PaymentTransactionId,
                IdempotencyKey = existing.IdempotencyKey,
                PaymentStatus = existing.Status,
                OrderStatus = existing.Order?.Status ?? OrderStatus.PendingPayment,
                IsIdempotentReplay = true,
                MessageId = string.Empty
            };
        }

        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid().ToString("N");

        var order = request.OrderId is { } existingOrderId
            ? await LoadOrderAsync(existingOrderId, cancellationToken)
              ?? throw new OrderNotFoundException(existingOrderId)
            : new Order
            {
                OrderId = Guid.NewGuid(),
                Status = OrderStatus.PendingPayment,
                TotalAmount = request.TotalAmount,
                Currency = request.Currency,
                PaymentMethod = request.PaymentMethod,
                CreatedAt = now,
                UpdatedAt = now,
                TraceId = TraceContextAccessor.TraceId
            };

        if (order.Status is OrderStatus.Paid or OrderStatus.Cancelled or OrderStatus.Refunded)
        {
            throw new InvalidPaymentStateException(
                $"Order '{order.OrderId}' is in status '{order.Status}' and cannot accept a new payment.");
        }

        var transaction = new PaymentTransaction
        {
            PaymentTransactionId = Guid.NewGuid(),
            OrderId = order.OrderId,
            IdempotencyKey = idempotencyKey,
            Amount = request.TotalAmount,
            Currency = request.Currency,
            Status = PaymentStatus.Pending,
            PaymentMethod = request.PaymentMethod,
            Scenario = request.Scenario,
            CreatedAt = now
        };

        order.TotalAmount = request.TotalAmount;
        order.Currency = request.Currency;
        order.PaymentMethod = request.PaymentMethod;
        order.UpdatedAt = now;

        var paymentRequested = new PaymentRequested
        {
            PaymentTransactionId = transaction.PaymentTransactionId,
            OrderId = order.OrderId,
            IdempotencyKey = idempotencyKey,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            PaymentMethod = transaction.PaymentMethod,
            Scenario = transaction.Scenario.ToString(),
            RequestedAt = now,
            TraceId = TraceContextAccessor.TraceId
        };

        var outbox = OutboxFactory.Create(
            PaymentTopology.PaymentRequestedType,
            PaymentTopology.EventsExchange,
            PaymentTopology.PaymentRequestedRoutingKey,
            paymentRequested,
            messageId);

        _db.Orders.Add(order);
        _db.PaymentTransactions.Add(transaction);
        _db.OutboxMessages.Add(outbox);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Corrida entre requisições com a mesma chave: o índice único
            // venceu. A transação inteira é desfeita (inclusive o pedido) e
            // a transação existente é devolvida.
            _db.ChangeTracker.Clear();

            var winner = await _db.PaymentTransactions
                .Include(t => t.Order)
                .FirstAsync(t => t.IdempotencyKey == idempotencyKey, cancellationToken);

            EnsureSameLogicalPayment(winner, request);

            _logger.LogInformation(
                "Duplicate payment request detected (concurrent). IdempotencyKey={IdempotencyKey} " +
                "PaymentTransactionId={PaymentTransactionId} PaymentStatus={PaymentStatus}",
                idempotencyKey,
                winner.PaymentTransactionId,
                winner.Status);

            return new OrderPaymentResult
            {
                OrderId = winner.OrderId,
                PaymentTransactionId = winner.PaymentTransactionId,
                IdempotencyKey = winner.IdempotencyKey,
                PaymentStatus = winner.Status,
                OrderStatus = winner.Order?.Status ?? OrderStatus.PendingPayment,
                IsIdempotentReplay = true,
                MessageId = string.Empty
            };
        }

        _logger.LogInformation(
            "Payment request persisted. OrderId={OrderId} PaymentTransactionId={PaymentTransactionId} " +
            "IdempotencyKey={IdempotencyKey} Amount={Amount} Currency={Currency} PaymentStatus={PaymentStatus} " +
            "MessageId={MessageId}",
            order.OrderId,
            transaction.PaymentTransactionId,
            idempotencyKey,
            transaction.Amount,
            transaction.Currency,
            transaction.Status,
            messageId);

        return new OrderPaymentResult
        {
            OrderId = order.OrderId,
            PaymentTransactionId = transaction.PaymentTransactionId,
            IdempotencyKey = idempotencyKey,
            PaymentStatus = transaction.Status,
            OrderStatus = order.Status,
            IsIdempotentReplay = false,
            MessageId = messageId
        };
    }

    private async Task<Order?> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken)
        => await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

    /// <summary>
    /// Reuso de chave com payload diferente é erro de negócio: o cliente
    /// está tentando(mutear) uma operação já registrada.
    /// </summary>
    private static void EnsureSameLogicalPayment(
        PaymentTransaction existing,
        CreateOrderPaymentRequest request)
    {
        var sameAmount = existing.Amount == request.TotalAmount;
        var sameCurrency = string.Equals(existing.Currency, request.Currency, StringComparison.OrdinalIgnoreCase);
        var sameMethod = string.Equals(existing.PaymentMethod, request.PaymentMethod, StringComparison.OrdinalIgnoreCase);
        var sameOrder = request.OrderId is null || request.OrderId == existing.OrderId;

        if (sameAmount && sameCurrency && sameMethod && sameOrder)
        {
            return;
        }

        throw new IdempotencyConflictException(
            $"Idempotency key '{existing.IdempotencyKey}' was already used for a different payment " +
            $"(existing: order={existing.OrderId}, " +
            $"amount={existing.Amount.ToString(CultureInfo.InvariantCulture)}, method={existing.PaymentMethod}; " +
            $"requested: order={request.OrderId?.ToString() ?? "<new>"}, " +
            $"amount={request.TotalAmount.ToString(CultureInfo.InvariantCulture)}, " +
            $"method={request.PaymentMethod}).");
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
