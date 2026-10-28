namespace AspirePayments.Worker;

/// <summary>Ajustes do worker de processamento de pagamentos.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Quando falso, o worker não assina as filas: as mensagens ficam
    /// paradas (ready) no RabbitMQ e podem ser inspecionadas antes do
    /// processamento. Também pode ser alternado em tempo de execução pelo
    /// endpoint de desenvolvimento.
    /// </summary>
    public bool ConsumingEnabled { get; set; } = true;

    /// <summary>
    /// Espera entre receber a mensagem e confirmar (ack). Durante esse
    /// tempo a mensagem fica visível na fila como "unacknowledged".
    /// </summary>
    public int ProcessingDelayMilliseconds { get; set; }

    /// <summary>Quantas tentativas antes de rotear a mensagem para a dead-letter queue.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Mensagens em trânsito por vez. Com 1, a próxima fica parada na fila.</summary>
    public ushort PrefetchCount { get; set; } = 1;
}
