using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TaxDoc.Core.Messaging;

namespace TaxDoc.Api.Messaging;

public interface IIngestPublisher
{
    Task PublishAsync(IngestMessage message, CancellationToken ct = default);
}

/// <summary>
/// Publishes to the durable "ingest" queue (default exchange, routing key = queue name).
/// One long-lived connection (thread-safe); a short-lived channel per publish, because channels aren't.
/// </summary>
public sealed class RabbitMqIngestPublisher(IOptions<RabbitMqOptions> options) : IIngestPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnection? _connection;

    public async Task PublishAsync(IngestMessage message, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await RabbitMqOptions.DeclareIngestQueueAsync(channel, ct);

        var props = new BasicProperties
        {
            Persistent = true, // survive a RabbitMQ restart (the queue is durable too)
            ContentType = "application/json",
        };
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: RabbitMqOptions.IngestQueue,
            mandatory: false,
            basicProperties: props,
            body: message.ToBytes(),
            cancellationToken: ct);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            _connection = await options.Value.CreateConnectionFactory().CreateConnectionAsync("taxdoc-api", ct);
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _connectLock.Dispose();
    }
}
