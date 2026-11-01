using Microsoft.EntityFrameworkCore;
using TaxDoc.Core.Documents;

namespace TaxDoc.Core.Data;

public class TaxDocDbContext(DbContextOptions<TaxDocDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Output size of the embedding model (qwen3-embedding:0.6b = 1024).
    /// Changing models means changing this and adding a migration.
    /// </summary>
    public const int EmbeddingDimensions = 1024;

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<Chunk> Chunks => Set<Chunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // CREATE EXTENSION IF NOT EXISTS vector
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Document>(e =>
        {
            e.Property(d => d.FileName).HasMaxLength(260);
            e.Property(d => d.StoragePath).HasMaxLength(1024);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.HasMany(d => d.Chunks).WithOne(c => c.Document).HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chunk>(e =>
        {
            e.Property(c => c.Article).HasMaxLength(100);
            e.Property(c => c.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            e.HasIndex(c => new { c.DocumentId, c.Ordinal }).IsUnique();

            // Approximate nearest-neighbour index for cosine distance (the <=> operator).
            e.HasIndex(c => c.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");
        });
    }
}
