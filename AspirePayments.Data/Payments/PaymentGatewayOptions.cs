using AspirePayments.Data.Domain;

namespace AspirePayments.Data.Payments;

/// <summary>Ajustes do gateway de pagamento simulado.</summary>
public sealed class PaymentGatewayOptions
{
    public const string SectionName = "PaymentGateway";

    /// <summary>
    /// Latência simulada da chamada ao gateway. Existe para deixar o
    /// comportamento observável; não representa I/O real.
    /// </summary>
    public int SimulatedLatencyMilliseconds { get; set; } = 250;
}
