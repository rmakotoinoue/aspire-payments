using System.Threading.Channels;
using AspirePayments.Api.Messaging;
using AspirePayments.Data.Contracts;
using Xunit;

namespace AspirePayments.Tests.Unit;

/// <summary>
/// O hub SSE precisa fazer broadcast: cada cliente tem o seu canal. Um
/// Channel compartilhado seria uma fila de trabalho e dividiria os eventos
/// entre as abas, deixando cada uma com apenas parte das notificações.
/// </summary>
public class EventChannelTests
{
    [Fact]
    public async Task Publish_delivers_the_same_notification_to_every_subscriber()
    {
        var hub = new EventChannel();

        var first = hub.Subscribe();
        var second = hub.Subscribe();
        var notification = NewNotification();

        hub.Publish(notification);

        var receivedByFirst = await first.Reader.ReadAsync();
        var receivedBySecond = await second.Reader.ReadAsync();

        Assert.Same(notification, receivedByFirst);
        Assert.Same(notification, receivedBySecond);
    }

    [Fact]
    public async Task Unsubscribe_completes_the_subscription()
    {
        var hub = new EventChannel();

        var subscription = hub.Subscribe();
        hub.Unsubscribe(subscription);

        await Assert.ThrowsAsync<ChannelClosedException>(
            async () => await subscription.Reader.ReadAsync());
    }

    [Fact]
    public async Task After_unsubscribe_other_subscribers_keep_receiving_events()
    {
        var hub = new EventChannel();

        var removed = hub.Subscribe();
        var remaining = hub.Subscribe();

        hub.Unsubscribe(removed);

        var notification = NewNotification();
        hub.Publish(notification);

        var received = await remaining.Reader.ReadAsync();
        Assert.Same(notification, received);
    }

    private static PaymentStatusChanged NewNotification() => new()
    {
        OrderId = Guid.NewGuid(),
        PaymentTransactionId = Guid.NewGuid(),
        IdempotencyKey = "key",
        OrderStatus = "Paid",
        PaymentStatus = "Succeeded",
        Amount = 10m,
        Currency = "BRL",
        OccurredAt = DateTimeOffset.UtcNow,
        TraceId = string.Empty
    };
}
