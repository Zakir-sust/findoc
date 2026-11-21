using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
using FinDoc.Api.Chat;
using FinDoc.Api.Documents;
using FinDoc.Api.Messaging;
using FinDoc.Core.Data;
using FinDoc.Core.Messaging;
using FinDoc.Core.Metrics;
using FinDoc.Core.Retrieval;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFinDocDb(builder.Configuration.GetConnectionString("FinDoc")
    ?? throw new InvalidOperationException("Missing connection string 'FinDoc'."));

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<IIngestPublisher, RabbitMqIngestPublisher>();

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddSingleton<FileStore>();

// Retrieval for POST /chat. The question must be embedded with the same model the worker used for the chunks
// (FinDoc.Ingestion/Program.cs): a different model gives vectors in a different space, and the distances mean nothing.
var embeddings = builder.Configuration.GetSection("Embeddings");
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new OllamaApiClient(
    new Uri(embeddings["Endpoint"] ?? throw new InvalidOperationException("Missing Embeddings:Endpoint.")),
    embeddings["Model"] ?? throw new InvalidOperationException("Missing Embeddings:Model.")));
builder.Services.AddScoped<IRetriever, Retriever>(); // scoped like the DbContext it uses

// get_financial_metric for POST /chat (C17): reads the table checked in C16. The build copies data/metrics.json
// next to the API's dll, so the path doesn't depend on the folder `dotnet run` starts in.
builder.Services.AddSingleton(MetricsTable.Load(Path.Combine(AppContext.BaseDirectory, "data", "metrics.json")));
builder.Services.AddSingleton<FinancialMetricTool>();

// IChatClient for POST /chat: Ollama or Azure OpenAI, picked by Chat:Provider. Stops start-up if its settings are missing.
builder.Services.AddFinDocChatClient(builder.Configuration);

var app = builder.Build();

// Local learning project: apply EF migrations at start-up so `dotnet run` is enough.
// (In production you'd run migrations as a separate deploy step.)
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<FinDocDbContext>().Database.MigrateAsync();
}

app.MapGet("/", () => "FinDoc API");
app.MapDocumentEndpoints();
app.MapChatEndpoints();

app.Run();

// WebApplicationFactory<Program> in the C15 integration tests needs a public Program to point at.
public partial class Program;
