using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BuyList.Infrastructure.Persistence;

/// <summary>Lets `dotnet ef` create the context without booting the web host.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<BuyListDbContext>
{
    public BuyListDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BuyListDbContext>().UseSqlite("Data Source=buylist.db").Options);
}
