using System.Text.Json;

namespace FinDoc.Core.Messaging;

/// <summary>The body of a message on the "ingest" queue: which document the worker should process.</summary>
public sealed record IngestMessage(Guid DocumentId)
{
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static IngestMessage FromBytes(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize<IngestMessage>(body)
        ?? throw new InvalidOperationException("Empty ingest message.");
}
