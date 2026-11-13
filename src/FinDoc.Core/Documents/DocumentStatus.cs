namespace FinDoc.Core.Documents;

/// <summary>Stored as text in Postgres so rows are readable in psql.</summary>
public enum DocumentStatus
{
    /// <summary>The API saved the file and queued it on "ingest".</summary>
    Uploaded,

    /// <summary>The worker extracted the text. Chunking/embedding (C08/C09) not done yet.</summary>
    Extracted,

    /// <summary>Chunks with embeddings are saved. Set once C08 + C09 are in.</summary>
    Ingested,

    Failed,
}
