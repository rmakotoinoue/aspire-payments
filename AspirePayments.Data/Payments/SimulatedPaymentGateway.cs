using AspirePayments.Data.Domain;

namespace AspirePayments.Data.Payments;

/// <summary>
/// Gateway de pagamento determinístico. O cenário enviado pelo cliente
/// define o resultado, de modo que a demonstração é sempre reproduzível.
/// Nenhum dinheiro real é movimentado.
/// </summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway
{
    private readonly TimeSpan _latency;

    public SimulatedPaymentGateway(PaymentGatewayOptions options)
    {
        _latency = TimeSpan.FromMilliseconds(Math.Max(0, options.SimulatedLatencyMilliseconds));
    }

    public async Task<GatewayResult> ChargeAsync(
        PaymentScenario scenario,
        Guid orderId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken);

        return scenario switch
        {
            // Cenário A: aprovação.
            PaymentScenario.Succeed => Approved(),

            // Cenário D: o gateway aprova, mas o worker falha depois do
            // commit e antes do ack.
            PaymentScenario.FailAfterCommit => Approved(),

            // Cenário B: recusa definitiva do emissor.
            PaymentScenario.Fail => new GatewayResult(
                Approved: false,
                FailureReason: "Payment declined by issuer (insufficient funds)",
                GatewayReference: Reference()),

            // Cenário D: falha transitória que esgota as tentativas.
            PaymentScenario.FailTransient => throw new TransientPaymentException(
                "Simulated transient failure from the payment gateway"),

            _ => new GatewayResult(
                Approved: false,
                FailureReason: $"Unsupported payment scenario '{scenario}'",
                GatewayReference: Reference())
        };
    }

    public async Task<GatewayResult> RefundAsync(
        RefundScenario scenario,
        Guid orderId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken);

        return scenario switch
        {
            RefundScenario.Succeed => Approved(),
            RefundScenario.Fail => new GatewayResult(
                Approved: false,
                FailureReason: "Refund window expired",
                GatewayReference: Reference()),
            _ => new GatewayResult(
                Approved: false,
                FailureReason: $"Unsupported refund scenario '{scenario}'",
                GatewayReference: Reference())
        };
    }

    private async Task SimulateLatencyAsync(CancellationToken cancellationToken)
    {
        if (_latency > TimeSpan.Zero)
        {
            await Task.Delay(_latency, cancellationToken);
        }
    }

    private static GatewayResult Approved() => new(true, null, Reference());

    private static string Reference() => $"sim-{Guid.NewGuid():N}";
}
