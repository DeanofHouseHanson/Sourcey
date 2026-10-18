using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuyList.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("BuyList") ?? "Data Source=buylist.db";
        services.AddDbContext<BuyListDbContext>(o => o.UseSqlite(cs));
        return services;
    }
}
