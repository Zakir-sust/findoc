using Microsoft.EntityFrameworkCore;
using TaxDoc.Api.Documents;
using TaxDoc.Api.Messaging;
using TaxDoc.Core.Data;
using TaxDoc.Core.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaxDocDb(builder.Configuration.GetConnectionString("TaxDoc")
    ?? throw new InvalidOperationException("Missing connection string 'TaxDoc'."));

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<IIngestPublisher, RabbitMqIngestPublisher>();

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddSingleton<FileStore>();

var app = builder.Build();

// Local learning project: apply EF migrations at start-up so `dotnet run` is enough.
// (In production you'd run migrations as a separate deploy step.)
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<TaxDocDbContext>().Database.MigrateAsync();
}

app.MapGet("/", () => "TaxDoc API");
app.MapDocumentEndpoints();

app.Run();
