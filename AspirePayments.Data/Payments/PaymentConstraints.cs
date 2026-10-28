namespace AspirePayments.Data.Payments;

/// <summary>
/// Limites compartilhados entre os contratos da API e as regras de domínio.
/// Manter os valores em um único lugar evita que a validação da borda e a
/// restrição do banco divirjam.
/// </summary>
public static class PaymentConstraints
{
    /// <summary>Tamanho máximo da chave de idempotência (igual à coluna no banco).</summary>
    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>Tamanho máximo do motivo de um reembolso (igual à coluna no banco).</summary>
    public const int MaxReasonLength = 512;

    /// <summary>Tamanho máximo do método de pagamento (igual à coluna no banco).</summary>
    public const int MaxPaymentMethodLength = 64;

    /// <summary>Maior valor aceito em uma solicitação, para evitar valores absurdos.</summary>
    public const double MaxAmount = 1_000_000_000d;

    /// <summary>Moeda padrão da demonstração.</summary>
    public const string DefaultCurrency = "BRL";
}
