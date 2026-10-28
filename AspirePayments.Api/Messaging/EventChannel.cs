using System.Threading.Channels;
using AspirePayments.Data.Contracts;

namespace AspirePayments.Api.Messaging;

/// <summary>
/// Hub de distribuição das notificações de pagamento para os clientes SSE.
///
/// Importante: um <see cref="Channel{T}"/> compartilhado não serviria aqui.
/// Ele reparte as mensagens entre os leitores (é fila de trabalho), então
/// com duas abas abertas cada uma receberia apenas uma parte dos eventos.
/// Aqui cada assinante tem o seu próprio canal, o que faz a entrega ser uma
/// transmissão (broadcast) e não uma divisão.
/// </summary>
public sealed class EventChannel
{
    private const int SubscriberBufferSize = 100;

    private readonly object _gate = new();
    private readonly List<Channel<PaymentStatusChanged>> _subscribers = [];

    /// <summary>Cria uma assinatura. Toda notificação publicada chega nela.</summary>
    public Channel<PaymentStatusChanged> Subscribe()
    {
        var channel = Channel.CreateBounded<PaymentStatusChanged>(new BoundedChannelOptions(SubscriberBufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        return channel;
    }

    /// <summary>Encerra a assinatura e remove o cliente da lista de assinantes.</summary>
    public void Unsubscribe(Channel<PaymentStatusChanged> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    /// <summary>Entrega a notificação para todas as assinaturas ativas.</summary>
    public void Publish(PaymentStatusChanged notification)
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(notification);
            }
        }
    }
}
