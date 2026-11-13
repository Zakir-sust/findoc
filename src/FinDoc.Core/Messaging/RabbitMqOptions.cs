using RabbitMQ.Client;

namespace FinDoc.Core.Messaging;

/// <summary>Bound from the "RabbitMq" config section in both the API and the worker.</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>The durable queue the API publishes to and the worker consumes from.</summary>
    public const string IngestQueue = "ingest";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";

    public ConnectionFactory CreateConnectionFactory() => new()
    {
        HostName = HostName,
        Port = Port,
        UserName = UserName,
        Password = Password,
    };

    /// <summary>Idempotent: both sides declare the same durable queue, so start-up order doesn't matter.</summary>
    public static Task DeclareIngestQueueAsync(IChannel channel, CancellationToken ct = default) =>
        channel.QueueDeclareAsync(IngestQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: ct);
}
