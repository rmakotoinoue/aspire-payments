namespace AspirePayments.Data.Payments;

/// <summary>
/// Falha transitória no gateway: a operação deve ser repetida. Quando as
/// tentativas se esgotam, a mensagem vai para a dead-letter queue.
/// </summary>
public sealed class TransientPaymentException : Exception
{
    public TransientPaymentException(string message)
        : base(message)
    {
    }

    public TransientPaymentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Falha injetada depois do commit da transação de banco e antes do ack da
/// mensagem. Serve para demonstrar que a redelivery não duplica efeitos.
/// </summary>
public sealed class SimulatedWorkerCrashException : Exception
{
    public SimulatedWorkerCrashException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A chave de idempotência foi reutilizada com um payload materialmente
/// diferente (outro pedido, outro valor, outro método de pagamento).
/// </summary>
public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// O pagamento solicitado não pode ser concluído no estado atual (por
/// exemplo, um pedido já pago ou já cancelado).
/// </summary>
public sealed class InvalidPaymentStateException : Exception
{
    public InvalidPaymentStateException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// O pedido informado não existe. É um erro de recurso ausente (HTTP 404),
/// distinto de um pedido em estado inválido (HTTP 409).
/// </summary>
public sealed class OrderNotFoundException : Exception
{
    public OrderNotFoundException(Guid orderId)
        : base($"Order '{orderId}' does not exist.")
    {
        OrderId = orderId;
    }

    public Guid OrderId { get; }
}

/// <summary>
/// Os dados da solicitação são inválidos (valor não positivo, chave de
/// idempotência acima do limite, reembolso maior que o valor disponível).
/// É um erro do cliente, não uma falha de infraestrutura.
/// </summary>
public sealed class PaymentValidationException : Exception
{
    public PaymentValidationException(string message)
        : base(message)
    {
    }
}
