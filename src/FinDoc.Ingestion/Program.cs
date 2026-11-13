using Microsoft.Extensions.AI;
using OllamaSharp;
using FinDoc.Core.Chunking;
using FinDoc.Core.Data;
using FinDoc.Core.Messaging;
using FinDoc.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFinDocDb(builder.Configuration.GetConnectionString("FinDoc")
    ?? throw new InvalidOperationException("Missing connection string 'FinDoc'."));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddScoped<DocumentIngestor>();
builder.Services.AddSingleton<IChunker, ArticleChunker>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new OllamaApiClient(new Uri("http://localhost:11434"), "qwen3-embedding:0.6b"));
builder.Services.AddHostedService<IngestWorker>();

var host = builder.Build();
host.Run();
