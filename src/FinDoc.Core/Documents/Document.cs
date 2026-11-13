namespace FinDoc.Core.Documents;

/// <summary>An uploaded annual-report (10-K) PDF and where it is in the ingestion pipeline.</summary>
public class Document
{
    public Guid Id { get; set; }

    /// <summary>The original file name, e.g. "apple-fy2026.pdf".</summary>
    public required string FileName { get; set; }

    /// <summary>Absolute path of the stored PDF (the API writes it, the worker reads it).</summary>
    public required string StoragePath { get; set; }

    public long SizeBytes { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;

    /// <summary>Filled by the worker after PdfPig extraction.</summary>
    public int? PageCount { get; set; }

    /// <summary>Characters of extracted text, filled by the worker.</summary>
    public int? TextLength { get; set; }

    /// <summary>Why ingestion failed, when <see cref="Status"/> is Failed.</summary>
    public string? Error { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public List<Chunk> Chunks { get; set; } = [];
}
