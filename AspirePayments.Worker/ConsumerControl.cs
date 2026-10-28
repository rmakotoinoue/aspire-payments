namespace AspirePayments.Worker;

/// <summary>
/// Estado de pausa do consumidor, compartilhado entre o worker e o endpoint
/// de desenvolvimento. Permite inspecionar as mensagens na fila antes de o
/// worker processá-las.
/// </summary>
public sealed class ConsumerControl
{
    private int _paused;

    public ConsumerControl(WorkerOptions options)
    {
        _paused = options.ConsumingEnabled ? 0 : 1;
    }

    /// <summary>True quando o worker não deve consumir mensagens.</summary>
    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    public void Pause() => Volatile.Write(ref _paused, 1);

    public void Resume() => Volatile.Write(ref _paused, 0);
}
