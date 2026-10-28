using AspirePayments.Data.Contracts;
using AspirePayments.Data.Entities;
using AspirePayments.Data.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AspirePayments.Data.Messaging;

/// <summary>
/// Despachante da transactional outbox. Lê as mensagens pendentes, publica
/// no RabbitMQ e marca como publicadas.
///
/// A seleção usa <c>FOR UPDATE SKIP LOCKED</c>, então várias instâncias do
/// despachante (API e Worker) nunca processam a mesma mensagem. A publicação
/// é <i>at-least-once</i>: em caso de falha entre publicar e confirmar, a
/// mensagem pode ser publicada de novo — por isso o consumidor precisa ser
/// idempotente.
/// </summary>
public sealed class OutboxPublisher : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        IServiceProvider services,
        IOptions<OutboxOptions> options,
        ILogger<OutboxPublisher> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox publisher started. PollInterval={PollInterval}ms BatchSize={BatchSize}",
            _options.PollIntervalMilliseconds,
            _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(stoppingToken);

                if (published > 0)
                {
                    _logger.LogInformation(
                        "Outbox batch published. PublishedCount={PublishedCount}", published);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox publishing failed. It will be retried on the next poll.");
            }

            await Task.Delay(_options.PollIntervalMilliseconds, stoppingToken);
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var connection = scope.ServiceProvider.GetRequiredService<IConnection>();
        var publisher = scope.ServiceProvider.GetRequiredService<RabbitMqMessagePublisher>();

        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await PaymentTopology.DeclareAsync(channel, cancellationToken);

        await using var dbTransaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var messages = await db.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY created_at
                LIMIT {_options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            await dbTransaction.CommitAsync(cancellationToken);
            return 0;
        }

        foreach (var message in messages)
        {
            var extraHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MessageHeaders.MessageId] = message.MessageId,
                [MessageHeaders.MessageType] = message.MessageType
            };

            await publisher.PublishAsync(
                channel,
                message.Exchange,
                message.RoutingKey,
                message.MessageType,
                message.MessageId,
                message.Payload,
                message.TraceParent,
                message.TraceState,
                extraHeaders,
                cancellationToken);

            message.PublishedAt = DateTimeOffset.UtcNow;
            message.PublishAttempts += 1;
            message.LastError = null;

            _logger.LogInformation(
                "Outbox message published to RabbitMQ. MessageId={MessageId} MessageType={MessageType} " +
                "Exchange={Exchange} RoutingKey={RoutingKey} TraceId={TraceId}",
                message.MessageId,
                message.MessageType,
                message.Exchange,
                message.RoutingKey,
                message.TraceParent);
        }

        await db.SaveChangesAsync(cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);

        return messages.Count;
    }
}
