using AspirePayments.Data.Domain;

namespace AspirePayments.Data.Payments;

/// <summary>Resultado de uma operação no gateway de pagamento simulado.</summary>
/// <param name="Approved">Indica se a operação financeira foi aprovada.</param>
/// <param name="FailureReason">Motivo da recusa, quando houver.</param>
/// <param name="GatewayReference">Referência retornada pelo gateway.</param>
public sealed record GatewayResult(bool Approved, string? FailureReason, string GatewayReference);

/// <summary>Gateway de pagamento simulado. Nenhum valor real é movimentado.</summary>
public interface IPaymentGateway
{
    Task<GatewayResult> ChargeAsync(
        PaymentScenario scenario,
        Guid orderId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken);

    Task<GatewayResult> RefundAsync(
        RefundScenario scenario,
        Guid orderId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken);
}
