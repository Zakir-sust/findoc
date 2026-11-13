using Microsoft.Extensions.AI;
using OllamaSharp;
using TaxDoc.Core.Chunking;
using TaxDoc.Core.Data;
using TaxDoc.Core.Messaging;
using TaxDoc.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTaxDocDb(builder.Configuration.GetConnectionString("TaxDoc")
    ?? throw new InvalidOperationException("Missing connection string 'TaxDoc'."));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddScoped<DocumentIngestor>();
builder.Services.AddSingleton<IChunker, ArticleChunker>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new OllamaApiClient(new Uri("http://localhost:11434"), "qwen3-embedding:0.6b"));
builder.Services.AddHostedService<IngestWorker>();

var host = builder.Build();
host.Run();
