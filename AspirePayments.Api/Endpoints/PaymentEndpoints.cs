using AspirePayments.Api.Contracts;
using AspirePayments.Api.Validation;
using AspirePayments.Data;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AspirePayments.Api.Endpoints;

/// <summary>Endpoints de pedidos e pagamentos.</summary>
public static class PaymentEndpoints
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Payments")
            .AddEndpointFilter<ValidationEndpointFilter>();

        // Cria um pedido e solicita o pagamento. Idempotente pela chave.
        group.MapPost("/orders", CreateOrder)
            .WithName("CreateOrder")
            .WithSummary("Creates an order and requests its payment.");

        group.MapGet("/orders", ListOrders)
            .WithName("ListOrders")
            .WithSummary("Lists the most recent orders.");

        group.MapGet("/orders/{orderId:guid}", GetOrder)
            .WithName("GetOrder")
            .WithSummary("Returns an order with its payments and refunds.");

        group.MapGet("/orders/{orderId:guid}/payments", GetOrderPayments)
            .WithName("GetOrderPayments")
            .WithSummary("Returns the payment history of an order.");

        group.MapPost("/orders/{orderId:guid}/payments", CreatePaymentForOrder)
            .WithName("CreatePaymentForOrder")
            .WithSummary("Requests a payment for an existing order.");

        group.MapGet("/payments/{paymentTransactionId:guid}", GetPayment)
            .WithName("GetPayment")
            .WithSummary("Returns a single payment transaction.");

        group.MapPost("/orders/{orderId:guid}/refunds", CreateRefund)
            .WithName("CreateRefund")
            .WithSummary("Requests a refund of a captured payment.");

        return app;
    }

    private static Task<Results<Accepted<OrderPaymentResponse>, Conflict<ProblemDetails>, NotFound<ProblemDetails>, ValidationProblem>>
        CreateOrder(
            CreateOrderRequest request,
            HttpContext httpContext,
            OrderPaymentService service,
            CancellationToken cancellationToken)
        => CreatePaymentAsync(orderId: null, request, httpContext, service, cancellationToken);

    private static Task<Results<Accepted<OrderPaymentResponse>, Conflict<ProblemDetails>, NotFound<ProblemDetails>, ValidationProblem>>
        CreatePaymentForOrder(
            Guid orderId,
            CreateOrderRequest request,
            HttpContext httpContext,
            OrderPaymentService service,
            CancellationToken cancellationToken)
        => CreatePaymentAsync(orderId, request, httpContext, service, cancellationToken);

    private static async Task<Results<Accepted<OrderPaymentResponse>, Conflict<ProblemDetails>, NotFound<ProblemDetails>, ValidationProblem>>
        CreatePaymentAsync(
            Guid? orderId,
            CreateOrderRequest request,
            HttpContext httpContext,
            OrderPaymentService service,
            CancellationToken cancellationToken)
    {
        if (!PaymentMappings.TryParseScenario(request.Scenario, out var scenario))
        {
            return InvalidScenario(nameof(request.Scenario), request.Scenario, Enum.GetNames<PaymentScenario>());
        }

        var idempotencyKey = ResolveIdempotencyKey(httpContext, request.IdempotencyKey);

        if (idempotencyKey is { Length: > PaymentConstraints.MaxIdempotencyKeyLength })
        {
            return ValidationError(
                nameof(request.IdempotencyKey),
                $"Idempotency key must be at most {PaymentConstraints.MaxIdempotencyKeyLength} characters long.");
        }

        try
        {
            var result = await service.CreateAsync(
                new CreateOrderPaymentRequest
                {
                    TotalAmount = request.TotalAmount,
                    Currency = request.Currency,
                    PaymentMethod = request.PaymentMethod,
                    Scenario = scenario,
                    IdempotencyKey = idempotencyKey,
                    OrderId = orderId
                },
                cancellationToken);

            return TypedResults.Accepted($"/api/orders/{result.OrderId}", ToResponse(result));
        }
        catch (OrderNotFoundException ex)
        {
            return OrderNotFound(ex.OrderId);
        }
        catch (IdempotencyConflictException ex)
        {
            return Conflict("Idempotency key conflict", ex.Message);
        }
        catch (InvalidPaymentStateException ex)
        {
            return Conflict("Invalid payment state", ex.Message);
        }
        catch (PaymentValidationException ex)
        {
            return ValidationError("request", ex.Message);
        }
    }

    private static async Task<Ok<IReadOnlyList<OrderResponse>>> ListOrders(
        PaymentsDbContext db,
        CancellationToken cancellationToken)
    {
        var orders = await db.Orders
            .AsNoTracking()
            .Include(o => o.PaymentTransactions)
                .ThenInclude(t => t.ProcessingRecords)
            .Include(o => o.Refunds)
            .OrderByDescending(o => o.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyList<OrderResponse>>(
            orders.Select(o => o.ToResponse()).ToList());
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound<ProblemDetails>>> GetOrder(
        Guid orderId,
        PaymentsDbContext db,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.PaymentTransactions)
                .ThenInclude(t => t.ProcessingRecords)
            .Include(o => o.Refunds)
            .FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

        if (order is null)
        {
            return OrderNotFound(orderId);
        }

        return TypedResults.Ok(order.ToResponse());
    }

    private static async Task<Results<Ok<IReadOnlyList<PaymentTransactionResponse>>, NotFound<ProblemDetails>>>
        GetOrderPayments(
            Guid orderId,
            PaymentsDbContext db,
            CancellationToken cancellationToken)
    {
        var orderExists = await db.Orders
            .AsNoTracking()
            .AnyAsync(o => o.OrderId == orderId, cancellationToken);

        if (!orderExists)
        {
            return OrderNotFound(orderId);
        }

        var payments = await db.PaymentTransactions
            .AsNoTracking()
            .Include(t => t.ProcessingRecords)
            .Where(t => t.OrderId == orderId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyList<PaymentTransactionResponse>>(
            payments.Select(p => p.ToResponse()).ToList());
    }

    private static async Task<Results<Ok<PaymentTransactionResponse>, NotFound<ProblemDetails>>> GetPayment(
        Guid paymentTransactionId,
        PaymentsDbContext db,
        CancellationToken cancellationToken)
    {
        var payment = await db.PaymentTransactions
            .AsNoTracking()
            .Include(t => t.ProcessingRecords)
            .FirstOrDefaultAsync(t => t.PaymentTransactionId == paymentTransactionId, cancellationToken);

        if (payment is null)
        {
            return TypedResults.NotFound(new ProblemDetails
            {
                Title = "Payment transaction not found",
                Detail = $"Payment transaction '{paymentTransactionId}' does not exist.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return TypedResults.Ok(payment.ToResponse());
    }

    private static async Task<Results<Accepted<RefundResponse>, Conflict<ProblemDetails>, NotFound<ProblemDetails>, ValidationProblem>>
        CreateRefund(
            Guid orderId,
            CreateRefundRequest request,
            HttpContext httpContext,
            RefundService service,
            CancellationToken cancellationToken)
    {
        if (!PaymentMappings.TryParseRefundScenario(request.Scenario, out var scenario))
        {
            return InvalidScenario(nameof(request.Scenario), request.Scenario, Enum.GetNames<RefundScenario>());
        }

        var idempotencyKey = ResolveIdempotencyKey(httpContext, request.IdempotencyKey);

        if (idempotencyKey is { Length: > PaymentConstraints.MaxIdempotencyKeyLength })
        {
            return ValidationError(
                nameof(request.IdempotencyKey),
                $"Idempotency key must be at most {PaymentConstraints.MaxIdempotencyKeyLength} characters long.");
        }

        try
        {
            var result = await service.CreateAsync(
                new CreateRefundCommand
                {
                    OrderId = orderId,
                    PaymentTransactionId = request.PaymentTransactionId,
                    Amount = request.Amount,
                    Currency = PaymentConstraints.DefaultCurrency,
                    Scenario = scenario,
                    Reason = request.Reason,
                    IdempotencyKey = idempotencyKey
                },
                cancellationToken);

            return TypedResults.Accepted($"/api/orders/{orderId}", new RefundResponse
            {
                RefundId = result.RefundId,
                OrderId = result.OrderId,
                PaymentTransactionId = result.PaymentTransactionId,
                IdempotencyKey = result.IdempotencyKey,
                RefundStatus = result.Status.ToString(),
                IsIdempotentReplay = result.IsIdempotentReplay,
                MessageId = result.MessageId
            });
        }
        catch (OrderNotFoundException ex)
        {
            return OrderNotFound(ex.OrderId);
        }
        catch (IdempotencyConflictException ex)
        {
            return Conflict("Idempotency key conflict", ex.Message);
        }
        catch (InvalidPaymentStateException ex)
        {
            return Conflict("Invalid refund state", ex.Message);
        }
        catch (PaymentValidationException ex)
        {
            return ValidationError("request", ex.Message);
        }
    }

    private static string? ResolveIdempotencyKey(HttpContext httpContext, string? bodyKey)
    {
        var headerKey = httpContext.Request.Headers[IdempotencyKeyHeader].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(headerKey))
        {
            return headerKey.Trim();
        }

        return string.IsNullOrWhiteSpace(bodyKey) ? null : bodyKey.Trim();
    }

    private static Conflict<ProblemDetails> Conflict(string title, string detail)
        => TypedResults.Conflict(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status409Conflict
        });

    private static ValidationProblem ValidationError(string field, string message)
        => TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });

    private static ValidationProblem InvalidScenario(
        string field,
        string? value,
        IReadOnlyCollection<string> allowedValues)
        => ValidationError(
            field,
            $"Unknown value '{value}'. Allowed values: {string.Join(", ", allowedValues)}.");

    private static NotFound<ProblemDetails> OrderNotFound(Guid orderId)
        => TypedResults.NotFound(new ProblemDetails
        {
            Title = "Order not found",
            Detail = $"Order '{orderId}' does not exist.",
            Status = StatusCodes.Status404NotFound
        });

    private static OrderPaymentResponse ToResponse(OrderPaymentResult result)
        => new()
        {
            OrderId = result.OrderId,
            PaymentTransactionId = result.PaymentTransactionId,
            IdempotencyKey = result.IdempotencyKey,
            PaymentStatus = result.PaymentStatus.ToString(),
            OrderStatus = result.OrderStatus.ToString(),
            IsIdempotentReplay = result.IsIdempotentReplay,
            MessageId = result.MessageId
        };
}
