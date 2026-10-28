namespace AspirePayments.Data.Domain;

/// <summary>
/// Comportamento determinístico do gateway simulado. O cenário viaja dentro
/// da mensagem, então o worker reproduz exatamente o resultado esperado
/// durante a demonstração.
/// </summary>
public enum PaymentScenario
{
    /// <summary>Cenário A: o gateway aprova o pagamento.</summary>
    Succeed = 0,

    /// <summary>Cenário B: o gateway recusa o pagamento (valor não autorizado).</summary>
    Fail = 1,

    /// <summary>
    /// Cenário D: o worker confirma a transação no banco e falha depois disso,
    /// antes do ack. A mensagem volta e o reprocessamento não pode duplicar
    /// efeitos de negócio.
    /// </summary>
    FailAfterCommit = 2,

    /// <summary>
    /// Cenário D: falha transitória permanente. Força o esgotamento das
    /// tentativas e o roteamento para a dead-letter queue.
    /// </summary>
    FailTransient = 3
}
