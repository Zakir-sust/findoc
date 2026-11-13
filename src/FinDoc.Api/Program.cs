using Microsoft.EntityFrameworkCore;
using FinDoc.Api.Chat;
using FinDoc.Api.Documents;
using FinDoc.Api.Messaging;
using FinDoc.Core.Data;
using FinDoc.Core.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFinDocDb(builder.Configuration.GetConnectionString("FinDoc")
    ?? throw new InvalidOperationException("Missing connection string 'FinDoc'."));

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<IIngestPublisher, RabbitMqIngestPublisher>();

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddSingleton<FileStore>();

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
