namespace AspirePayments.Data.Domain;

/// <summary>Motivo pelo qual um reembolso é recusado.</summary>
public enum RefundScenario
{
    /// <summary>Reembolso aprovado.</summary>
    Succeed = 0,

    /// <summary>Reembolso recusado (janela de estorno expirada).</summary>
    Fail = 1
}
