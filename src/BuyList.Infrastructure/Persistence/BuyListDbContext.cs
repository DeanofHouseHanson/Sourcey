using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuyList.Infrastructure.Persistence;

public class BuyListDbContext : DbContext
{
    public BuyListDbContext(DbContextOptions<BuyListDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<SourceOffer> SourceOffers => Set<SourceOffer>();
    public DbSet<AmazonSnapshot> AmazonSnapshots => Set<AmazonSnapshot>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<BlocklistEntry> BlocklistEntries => Set<BlocklistEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HaveConversion<DecimalConverter>();
        builder.Properties<decimal?>().HaveConversion<NullableDecimalConverter>();
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    // SQLite has no decimal type: EF stores it as TEXT, which can't be sorted or summed in SQL. Decimals are
    // stored as integers in 1/10000ths (exact for prices and percentages) so ORDER BY and SUM work.
    private static long ToStored(decimal v) => (long)Math.Round(v * 10000m, 0, MidpointRounding.AwayFromZero);

    private sealed class DecimalConverter : ValueConverter<decimal, long>
    {
        public DecimalConverter() : base(v => ToStored(v), v => v / 10000m) { }
    }

    private sealed class NullableDecimalConverter : ValueConverter<decimal?, long?>
    {
        public NullableDecimalConverter() : base(v => v == null ? null : ToStored(v.Value), v => v == null ? null : v / 10000m) { }
    }

    // All timestamps are UTC; SQLite drops the Kind, so restore it on read.
    private sealed class UtcConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcConverter() : base(v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }

    private sealed class NullableUtcConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcConverter() : base(v => v == null ? null : v.Value.ToUniversalTime(),
            v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) { }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.HasIndex(p => p.Asin).IsUnique();   // SQLite unique indexes allow multiple NULLs
            e.HasIndex(p => p.Ean);
            e.HasIndex(p => p.Mpn);
            e.HasIndex(p => p.Brand);
            e.Property(p => p.Asin).HasMaxLength(10);
            e.Property(p => p.Ean).HasMaxLength(14);
            e.Property(p => p.ManualFlags).HasConversion<int>();
        });

        b.Entity<SourceOffer>(e =>
        {
            e.Property(o => o.RetailerName).IsRequired().HasMaxLength(200);
            e.Property(o => o.SourceUrl).IsRequired().HasMaxLength(2048);
            e.HasIndex(o => o.SourceUrl).IsUnique();
            e.HasIndex(o => o.RawEan);
            e.HasIndex(o => o.CheckedAt);
            e.HasOne(o => o.Product).WithMany(p => p.SourceOffers).HasForeignKey(o => o.ProductId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AmazonSnapshot>(e =>
        {
            e.HasIndex(s => new { s.ProductId, s.CapturedAt });
            e.HasOne(s => s.Product).WithMany(p => p.Snapshots).HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Opportunity>(e =>
        {
            e.Property(o => o.Flags).HasConversion<int>();
            e.HasIndex(o => o.SourceOfferId).IsUnique();
            e.HasIndex(o => o.RiskAdjustedMonthlyProfit);
            e.HasIndex(o => o.Status);
            e.HasOne(o => o.Product).WithMany().HasForeignKey(o => o.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.SourceOffer).WithMany().HasForeignKey(o => o.SourceOfferId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BlocklistEntry>(e =>
        {
            e.Property(x => x.Value).IsRequired().HasMaxLength(200);
            e.HasIndex(x => new { x.Type, x.Value }).IsUnique();
        });

        // Enums are stored as readable strings; the [Flags] columns are configured as int above.
        foreach (var prop in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
            if (type.IsEnum && type != typeof(OpportunityFlags))
                prop.SetProviderClrType(typeof(string));
        }
    }
}
