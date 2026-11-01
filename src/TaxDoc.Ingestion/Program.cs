using TaxDoc.Core.Data;
using TaxDoc.Core.Messaging;
using TaxDoc.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTaxDocDb(builder.Configuration.GetConnectionString("TaxDoc")
    ?? throw new InvalidOperationException("Missing connection string 'TaxDoc'."));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddScoped<DocumentIngestor>();
// TODO(C08/C09 — Jakir): register the IChunker and the embedding generator here.

builder.Services.AddHostedService<IngestWorker>();

var host = builder.Build();
host.Run();
