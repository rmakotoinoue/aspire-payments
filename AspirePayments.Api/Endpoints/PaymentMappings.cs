using AspirePayments.Api.Contracts;
using AspirePayments.Data.Domain;
using AspirePayments.Data.Entities;
using AspirePayments.Data.Payments;
using Microsoft.EntityFrameworkCore;

namespace AspirePayments.Api.Endpoints;

/// <summary>Mapeamento entre entidades e DTOs da API.</summary>
internal static class PaymentMappings
{
    public static PaymentTransactionResponse ToResponse(this PaymentTransaction transaction)
        => new()
        {
            PaymentTransactionId = transaction.PaymentTransactionId,
            OrderId = transaction.OrderId,
            IdempotencyKey = transaction.IdempotencyKey,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            Status = transaction.Status.ToString(),
            PaymentMethod = transaction.PaymentMethod,
            Scenario = transaction.Scenario.ToString(),
            FailureReason = transaction.FailureReason,
            CreatedAt = transaction.CreatedAt,
            ProcessedAt = transaction.ProcessedAt,
            ProcessingRecords = transaction.ProcessingRecords
                .OrderBy(r => r.CreatedAt)
                .Select(r => new ProcessingRecordResponse
                {
                    ProcessingRecordId = r.ProcessingRecordId,
                    MessageId = r.MessageId,
                    ProcessingStatus = r.ProcessingStatus.ToString(),
                    AttemptNumber = r.AttemptNumber,
                    ErrorDetails = r.ErrorDetails,
                    CreatedAt = r.CreatedAt
                })
                .ToList()
        };

    public static RefundResponseView ToResponse(this Refund refund)
        => new()
        {
            RefundId = refund.RefundId,
            OrderId = refund.OrderId,
            PaymentTransactionId = refund.PaymentTransactionId,
            IdempotencyKey = refund.IdempotencyKey,
            Amount = refund.Amount,
            Status = refund.Status.ToString(),
            Reason = refund.Reason,
            CreatedAt = refund.CreatedAt,
            CompletedAt = refund.CompletedAt
        };

    public static OrderResponse ToResponse(this Order order)
        => new()
        {
            OrderId = order.OrderId,
            Status = order.Status.ToString(),
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            PaymentMethod = order.PaymentMethod,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            TraceId = order.TraceId,
            Payments = order.PaymentTransactions
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.ToResponse())
                .ToList(),
            Refunds = order.Refunds
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.ToResponse())
                .ToList()
        };

    public static bool TryParseScenario(string? value, out PaymentScenario scenario)
        => Enum.TryParse(value, ignoreCase: true, out scenario)
           && !int.TryParse(value, out _)
           && Enum.IsDefined(scenario);

    public static bool TryParseRefundScenario(string? value, out RefundScenario scenario)
        => Enum.TryParse(value, ignoreCase: true, out scenario)
           && !int.TryParse(value, out _)
           && Enum.IsDefined(scenario);
}
