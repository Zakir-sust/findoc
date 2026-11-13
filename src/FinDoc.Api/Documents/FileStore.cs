using Microsoft.Extensions.Options;

namespace FinDoc.Api.Documents;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Where uploaded PDFs go. Relative paths resolve against the API's content root.</summary>
    public string UploadsPath { get; set; } = "../../storage/uploads";
}

/// <summary>Stores uploaded PDFs on local disk (gitignored). The worker reads them by absolute path.</summary>
public sealed class FileStore(IOptions<StorageOptions> options, IWebHostEnvironment env)
{
    private readonly string _root = Path.GetFullPath(options.Value.UploadsPath, env.ContentRootPath);

    public async Task<string> SaveAsync(Guid documentId, IFormFile file, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{documentId}.pdf");

        await using var stream = File.Create(path);
        await file.CopyToAsync(stream, ct);
        return path;
    }
}
