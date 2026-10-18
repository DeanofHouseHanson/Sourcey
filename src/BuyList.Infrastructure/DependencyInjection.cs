using BuyList.Application.Enrichment;
using BuyList.Application.Import;
using BuyList.Application.Keepa;
using BuyList.Application.Opportunities;
using BuyList.Application.Options;
using BuyList.Infrastructure.Enrichment;
using BuyList.Infrastructure.Keepa;
using BuyList.Infrastructure.Opportunities;
using BuyList.Infrastructure.Refresh;
using BuyList.Infrastructure.Seeding;
using BuyList.Infrastructure.Import;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;

namespace BuyList.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("BuyList") ?? "Data Source=buylist.db";
        services.AddDbContext<BuyListDbContext>(o => o.UseSqlite(cs));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IProductMatcher, ProductMatcher>();
        services.AddScoped<IOfferImportService, OfferImportService>();

        // Keepa: the real client is an HttpClient typed client with logging stripped (the API key is in its query string).
        services.AddSingleton<KeepaTokenState>();
        services.AddHttpClient<KeepaClient>()
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            });
        services.AddSingleton<FakeKeepaClient>();
        services.AddScoped<IKeepaClient>(sp => sp.GetRequiredService<IOptions<KeepaOptions>>().Value.UseFake
            ? sp.GetRequiredService<FakeKeepaClient>()
            : sp.GetRequiredService<KeepaClient>());

        services.AddScoped<IEnrichmentService, EnrichmentService>();
        services.AddScoped<IOpportunityService, OpportunityService>();
        services.AddScoped<IRefreshPipeline, RefreshPipeline>();
        services.AddScoped<DemoSeeder>();
        return services;
    }
}
