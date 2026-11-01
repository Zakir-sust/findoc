using Microsoft.EntityFrameworkCore;
using TaxDoc.Api.Messaging;
using TaxDoc.Core.Data;
using TaxDoc.Core.Documents;
using TaxDoc.Core.Messaging;

namespace TaxDoc.Api.Documents;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/documents");

        // curl -F "file=@sources/Treaty-UK-7-24-2001.pdf" http://localhost:5080/documents
        group.MapPost("/", UploadAsync)
            .DisableAntiforgery(); // plain multipart upload from curl/Angular, no cookies involved

        group.MapGet("/{id:guid}", GetAsync);

        return app;
    }

    /// <summary>Save the PDF, record it, then queue it. The worker does the slow part.</summary>
    private static async Task<IResult> UploadAsync(
        IFormFile file, FileStore fileStore, TaxDocDbContext db, IIngestPublisher publisher, CancellationToken ct)
    {
        if (file.Length == 0)
            return Results.BadRequest("The file is empty.");
        if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Only .pdf files are accepted.");

        var id = Guid.NewGuid();
        var storagePath = await fileStore.SaveAsync(id, file, ct);

        var document = new Document
        {
            Id = id,
            FileName = Path.GetFileName(file.FileName),
            StoragePath = storagePath,
            SizeBytes = file.Length,
            Status = DocumentStatus.Uploaded,
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);

        // Publish only after the row is committed, so the worker can always find it.
        // Known gap: if publishing fails here the document stays "Uploaded" with no message
        // (an outbox would fix that; out of scope for this project).
        await publisher.PublishAsync(new IngestMessage(id), ct);

        return Results.Accepted($"/documents/{id}", ToResponse(document));
    }

    private static async Task<IResult> GetAsync(Guid id, TaxDocDbContext db, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return document is null ? Results.NotFound() : Results.Ok(ToResponse(document));
    }

    private static DocumentResponse ToResponse(Document d) =>
        new(d.Id, d.FileName, d.Status.ToString(), d.SizeBytes, d.PageCount, d.TextLength, d.Error, d.UploadedAt, d.ProcessedAt);
}

public sealed record DocumentResponse(
    Guid Id,
    string FileName,
    string Status,
    long SizeBytes,
    int? PageCount,
    int? TextLength,
    string? Error,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ProcessedAt);
