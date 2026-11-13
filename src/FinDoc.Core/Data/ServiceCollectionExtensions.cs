using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinDoc.Core.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="FinDocDbContext"/> on Postgres with pgvector support. Shared by the API and the worker.</summary>
    public static IServiceCollection AddFinDocDb(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<FinDocDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
}
