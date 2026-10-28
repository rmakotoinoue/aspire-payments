using System.ComponentModel.DataAnnotations;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Payments;

namespace AspirePayments.Api.Contracts;

/// <summary>Corpo da criação de um pedido com pagamento.</summary>
public sealed record CreateOrderRequest
{
    [Range(0.01, PaymentConstraints.MaxAmount,
        ErrorMessage = "TotalAmount must be greater than zero.")]
    public required decimal TotalAmount { get; init; }

    [Required]
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "Currency must be a three-letter ISO 4217 code.")]
    public string Currency { get; init; } = PaymentConstraints.DefaultCurrency;

    [Required]
    [StringLength(PaymentConstraints.MaxPaymentMethodLength, MinimumLength = 1)]
    public string PaymentMethod { get; init; } = "SimulatedCard";

    /// <summary>
    /// Cenário determinístico do gateway simulado: Succeed, Fail,
    /// FailAfterCommit ou FailTransient.
    /// </summary>
    [Required]
    [StringLength(32)]
    public string Scenario { get; init; } = nameof(PaymentScenario.Succeed);

    /// <summary>
    /// Chave de idempotência. Também pode ser enviada no cabeçalho
    /// <c>Idempotency-Key</c>, que tem precedência.
    /// </summary>
    [StringLength(PaymentConstraints.MaxIdempotencyKeyLength)]
    public string? IdempotencyKey { get; init; }
}

/// <summary>Resposta da criação de um pedido com pagamento.</summary>
public sealed record OrderPaymentResponse
{
    public required Guid OrderId { get; init; }

    public required Guid PaymentTransactionId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string PaymentStatus { get; init; }

    public required string OrderStatus { get; init; }

    /// <summary>True quando a chave de idempotência já existia e nada foi refeito.</summary>
    public required bool IsIdempotentReplay { get; init; }

    /// <summary>Identificador da mensagem publicada (outbox).</summary>
    public required string MessageId { get; init; }
}

/// <summary>Corpo da solicitação de reembolso.</summary>
public sealed record CreateRefundRequest
{
    [Range(0.01, PaymentConstraints.MaxAmount,
        ErrorMessage = "Amount must be greater than zero.")]
    public required decimal Amount { get; init; }

    public Guid? PaymentTransactionId { get; init; }

    [Required]
    [StringLength(32)]
    public string Scenario { get; init; } = nameof(RefundScenario.Succeed);

    [StringLength(PaymentConstraints.MaxReasonLength)]
    public string? Reason { get; init; }

    [StringLength(PaymentConstraints.MaxIdempotencyKeyLength)]
    public string? IdempotencyKey { get; init; }
}

/// <summary>Resposta da solicitação de reembolso.</summary>
public sealed record RefundResponse
{
    public required Guid RefundId { get; init; }

    public required Guid OrderId { get; init; }

    public Guid? PaymentTransactionId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string RefundStatus { get; init; }

    public required bool IsIdempotentReplay { get; init; }

    public required string MessageId { get; init; }
}

/// <summary>Visão de uma transação de pagamento.</summary>
public sealed record PaymentTransactionResponse
{
    public required Guid PaymentTransactionId { get; init; }

    public required Guid OrderId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string Status { get; init; }

    public required string PaymentMethod { get; init; }

    public required string Scenario { get; init; }

    public string? FailureReason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public required IReadOnlyList<ProcessingRecordResponse> ProcessingRecords { get; init; }
}

/// <summary>Registro de uma tentativa de processamento.</summary>
public sealed record ProcessingRecordResponse
{
    public required Guid ProcessingRecordId { get; init; }

    public required string MessageId { get; init; }

    public required string ProcessingStatus { get; init; }

    public required int AttemptNumber { get; init; }

    public string? ErrorDetails { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Visão de um reembolso ou de uma compensação.</summary>
public sealed record RefundResponseView
{
    public required Guid RefundId { get; init; }

    public required Guid OrderId { get; init; }

    public Guid? PaymentTransactionId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required decimal Amount { get; init; }

    public required string Status { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Visão de um pedido com suas transações e reembolsos.</summary>
public sealed record OrderResponse
{
    public required Guid OrderId { get; init; }

    public required string Status { get; init; }

    public required decimal TotalAmount { get; init; }

    public required string Currency { get; init; }

    public required string PaymentMethod { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? TraceId { get; init; }

    public required IReadOnlyList<PaymentTransactionResponse> Payments { get; init; }

    public required IReadOnlyList<RefundResponseView> Refunds { get; init; }
}
