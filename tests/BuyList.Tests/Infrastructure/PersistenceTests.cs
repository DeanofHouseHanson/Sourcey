using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Tests.Infrastructure;

public sealed class PersistenceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    public PersistenceTests()
    {
        _conn.Open();
        using var db = NewContext();
        db.Database.Migrate();
    }

    private BuyListDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BuyListDbContext>().UseSqlite(_conn).Options);

    public void Dispose() => _conn.Dispose();

    private static SourceOffer Offer(string url = "https://shop.example/p/1", decimal ex = 10m) => new()
    {
        RetailerName = "Shop", SourceUrl = url, BuyPriceExVat = ex, BuyPriceIncVat = ex * 1.2m,
        CheckedAt = DateTime.UtcNow
    };

    [Fact]
    public void Migrations_apply_and_model_matches_snapshot()
    {
        using var db = NewContext();
        Assert.False(db.Database.GetPendingMigrations().Any());
    }

    [Fact]
    public void Decimals_round_trip_exactly()
    {
        using (var db = NewContext())
        {
            db.SourceOffers.Add(Offer(ex: 12.3456m));
            db.SaveChanges();
        }
        using var read = NewContext();
        Assert.Equal(12.3456m, read.SourceOffers.Single().BuyPriceExVat);
    }

    [Fact]
    public void Decimal_columns_can_be_ordered_and_summed_in_sql()
    {
        using var db = NewContext();
        db.SourceOffers.AddRange(Offer("https://a.example/1", 3m), Offer("https://a.example/2", 1m), Offer("https://a.example/3", 2m));
        db.SaveChanges();

        var ordered = db.SourceOffers.OrderBy(o => o.BuyPriceExVat).Select(o => o.BuyPriceExVat).ToList();
        Assert.Equal(new[] { 1m, 2m, 3m }, ordered);
        Assert.Equal(6m, db.SourceOffers.Sum(o => o.BuyPriceExVat));
    }

    [Fact]
    public void Timestamps_come_back_as_utc()
    {
        var t = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        using (var db = NewContext())
        {
            var o = Offer(); o.CheckedAt = t; db.SourceOffers.Add(o); db.SaveChanges();
        }
        using var read = NewContext();
        var back = read.SourceOffers.Single().CheckedAt;
        Assert.Equal(DateTimeKind.Utc, back.Kind);
        Assert.Equal(t, back);
    }

    [Fact]
    public void SourceUrl_is_not_nullable_in_schema()
    {
        using var db = NewContext();
        var prop = db.Model.FindEntityType(typeof(SourceOffer))!.FindProperty(nameof(SourceOffer.SourceUrl))!;
        Assert.False(prop.IsNullable);
    }

    [Fact]
    public void Duplicate_source_url_is_rejected()
    {
        using var db = NewContext();
        db.SourceOffers.Add(Offer("https://dup.example/x"));
        db.SaveChanges();
        db.SourceOffers.Add(Offer("https://dup.example/x"));
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Asin_is_unique_but_multiple_products_may_lack_one()
    {
        using var db = NewContext();
        db.Products.AddRange(new Product { Ean = "1" }, new Product { Ean = "2" });
        db.SaveChanges(); // two NULL asins are fine

        db.Products.AddRange(new Product { Asin = "B000000001" }, new Product { Asin = "B000000001" });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Enums_and_flags_round_trip()
    {
        using (var db = NewContext())
        {
            var p = new Product { Asin = "B000000002", ManualFlags = OpportunityFlags.Gated | OpportunityFlags.Hazmat };
            var o = Offer(); o.Product = p; o.StockStatus = StockStatus.InStock;
            db.Opportunities.Add(new Opportunity
            {
                Product = p, SourceOffer = o, RiskLevel = RiskLevel.Medium, Status = OpportunityStatus.Checked,
                Flags = OpportunityFlags.NoSalesData, CalculatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }
        using var read = NewContext();
        var opp = read.Opportunities.Include(x => x.Product).Single();
        Assert.Equal(RiskLevel.Medium, opp.RiskLevel);
        Assert.Equal(OpportunityStatus.Checked, opp.Status);
        Assert.Equal(OpportunityFlags.NoSalesData, opp.Flags);
        Assert.Equal(OpportunityFlags.Gated | OpportunityFlags.Hazmat, opp.Product!.ManualFlags);
    }

    [Fact]
    public void Blocklist_entries_are_unique_per_type_and_value()
    {
        using var db = NewContext();
        db.BlocklistEntries.Add(new BlocklistEntry { Type = BlocklistType.Brand, Value = "Nike" });
        db.SaveChanges();
        db.BlocklistEntries.Add(new BlocklistEntry { Type = BlocklistType.Brand, Value = "Nike" });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
