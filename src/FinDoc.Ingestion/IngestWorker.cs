using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using FinDoc.Core.Messaging;

namespace FinDoc.Ingestion;

/// <summary>
/// Consumes the "ingest" queue, one message at a time, with manual acks.
/// Ack only after <see cref="DocumentIngestor"/> has committed to Postgres: if the worker dies mid-way,
/// RabbitMQ redelivers the message instead of losing the document.
/// </summary>
public sealed class IngestWorker(
    IOptions<RabbitMqOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<IngestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await ConnectWithRetryAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await RabbitMqOptions.DeclareIngestQueueAsync(channel, stoppingToken);
        // prefetch 1: don't take a new message until the current one is acked.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            IngestMessage message;
            try
            {
                message = IngestMessage.FromBytes(ea.Body.Span);
            }
            catch (Exception ex)
            {
                // A malformed message will never succeed: drop it (no dead-letter queue in this project).
                logger.LogError(ex, "Malformed ingest message; rejecting");
                await channel.BasicRejectAsync(ea.DeliveryTag, requeue: false, stoppingToken);
                return;
            }

            try
            {
                logger.LogInformation("Received document {DocumentId}", message.DocumentId);

                // A fresh scope (and DbContext) per message.
                await using var scope = scopeFactory.CreateAsyncScope();
                var ingestor = scope.ServiceProvider.GetRequiredService<DocumentIngestor>();
                await ingestor.IngestAsync(message.DocumentId, stoppingToken);

                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
                logger.LogInformation("Acked document {DocumentId}", message.DocumentId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down: leave the message unacked; RabbitMQ redelivers it on the next start.
            }
            catch (Exception ex)
            {
                // The DB commit itself failed (e.g. Postgres down). Requeue to retry later.
                // Note: a permanent failure here would loop; acceptable for a local project.
                logger.LogError(ex, "Could not process document {DocumentId}; requeueing", message.DocumentId);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
            }
        };

        await channel.BasicConsumeAsync(RabbitMqOptions.IngestQueue, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Waiting for messages on '{Queue}'", RabbitMqOptions.IngestQueue);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <summary>RabbitMQ may still be starting when the worker starts (docker compose up), so retry a few times.</summary>
    private async Task<IConnection> ConnectWithRetryAsync(CancellationToken ct)
    {
        var factory = options.Value.CreateConnectionFactory();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await factory.CreateConnectionAsync("findoc-ingestion", ct);
            }
            catch (Exception ex) when (attempt < 10 && ex is not OperationCanceledException)
            {
                logger.LogWarning("RabbitMQ not reachable (attempt {Attempt}/10): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
}
