using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TaxDoc.Core.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="TaxDocDbContext"/> on Postgres with pgvector support. Shared by the API and the worker.</summary>
    public static IServiceCollection AddTaxDocDb(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<TaxDocDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
}
