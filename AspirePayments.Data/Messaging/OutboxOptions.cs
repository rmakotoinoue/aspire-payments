namespace AspirePayments.Data.Messaging;

/// <summary>Ajustes do despachante da outbox.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Intervalo entre varreduras da tabela de outbox.</summary>
    public int PollIntervalMilliseconds { get; set; } = 500;

    /// <summary>Quantidade máxima de mensagens publicadas por varredura.</summary>
    public int BatchSize { get; set; } = 20;

}
