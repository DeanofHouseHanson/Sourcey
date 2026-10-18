using System.Text;
using BuyList.Application.Import;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Tests.Import;

public sealed class OfferImportServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly FixedTime _time = new();
    private readonly OfferFileValidator _validator = OfferFileValidatorTests.Make();
    public void Dispose() => _testDb.Dispose();

    private const string Header = "retailer,url,ean,mpn,brand,title,price,vat_included,stock\n";

    private async Task<ImportSummary> Import(string csv)
    {
        var preview = _validator.Validate(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        using var db = _testDb.NewContext();
        var svc = new OfferImportService(db, new ProductMatcher(db), _time);
        return await svc.ImportAsync(preview.Valid, preview.Rejected.Count);
    }

    [Fact]
    public async Task Imports_new_offers_with_price_stock_source_url_and_checked_time()
    {
        var s = await Import(Header + "Shop,https://a.example/1,5012345678900,M-1,Acme,Widget,12.00,false,low stock\n");

        Assert.Equal(1, s.Added);
        using var db = _testDb.NewContext();
        var o = db.SourceOffers.Single();
        Assert.Equal("https://a.example/1", o.SourceUrl);
        Assert.Equal("Shop", o.RetailerName);
        Assert.Equal(12.00m, o.BuyPriceExVat);
        Assert.Equal(14.40m, o.BuyPriceIncVat);
        Assert.Equal(StockStatus.LowStock, o.StockStatus);
        Assert.Equal(_time.Now.UtcDateTime, o.CheckedAt);
        Assert.NotNull(o.ProductId);
    }

    [Fact]
    public async Task Rejected_rows_are_never_stored_but_are_counted()
    {
        var s = await Import(Header +
            "Shop,https://a.example/1,,,,Good,1.00,false,\n" +
            "Shop,,,,,Bad,1.00,false,\n");
        Assert.Equal(1, s.Added);
        Assert.Equal(1, s.Rejected);
        using var db = _testDb.NewContext();
        Assert.Equal(1, db.SourceOffers.Count());
    }

    [Fact]
    public async Task Reimport_updates_price_and_bumps_checked_time()
    {
        await Import(Header + "Shop,https://a.example/1,5012345678900,,,W,10.00,false,in stock\n");
        _time.Now = _time.Now.AddHours(30);
        var s = await Import(Header + "Shop,https://a.example/1,5012345678900,,,W,8.00,false,in stock\n");

        Assert.Equal(0, s.Added);
        Assert.Equal(1, s.Updated);
        using var db = _testDb.NewContext();
        var o = db.SourceOffers.Single();
        Assert.Equal(8.00m, o.BuyPriceExVat);
        Assert.Equal(_time.Now.UtcDateTime, o.CheckedAt);
    }

    [Fact]
    public async Task Reimport_of_unchanged_data_still_reconfirms_the_price()
    {
        await Import(Header + "Shop,https://a.example/1,,,,W,10.00,false,in stock\n");
        _time.Now = _time.Now.AddHours(5);
        var s = await Import(Header + "Shop,https://a.example/1,,,,W,10.00,false,in stock\n");

        Assert.Equal(1, s.Refreshed);
        Assert.Equal(0, s.Updated);
        using var db = _testDb.NewContext();
        Assert.Equal(_time.Now.UtcDateTime, db.SourceOffers.Single().CheckedAt);
        Assert.Equal(1, db.SourceOffers.Count());   // no duplicate row
    }

    [Fact]
    public async Task Changing_the_ean_on_reimport_rematches_the_offer()
    {
        await Import(Header + "Shop,https://a.example/1,5012345678900,,,W,10.00,false,\n");
        await Import(Header + "Shop,https://a.example/1,96385074,,,W,10.00,false,\n");

        using var db = _testDb.NewContext();
        var offer = db.SourceOffers.Include(o => o.Product).Single();
        Assert.Equal("96385074", offer.Product!.Ean);
    }

    [Fact]
    public async Task Same_ean_from_two_retailers_shares_a_product()
    {
        await Import(Header +
            "Shop A,https://a.example/1,5012345678900,,,W,10.00,false,\n" +
            "Shop B,https://b.example/9,5012345678900,,,W,9.00,false,\n");
        using var db = _testDb.NewContext();
        Assert.Equal(2, db.SourceOffers.Count());
        Assert.Equal(1, db.Products.Count());
    }

    [Fact]
    public async Task Unmatchable_offer_is_stored_with_no_product()
    {
        var s = await Import(Header + "Shop,https://a.example/1,,,,Mystery item,5.00,false,\n");
        Assert.Equal(1, s.Added);
        Assert.Equal(1, s.Matching.Unmatchable);
        using var db = _testDb.NewContext();
        Assert.Null(db.SourceOffers.Single().ProductId);
    }

    [Fact]
    public async Task The_full_sample_file_imports_and_matches_every_row()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", "offers.sample.csv"))) dir = dir.Parent;
        var s = await Import(File.ReadAllText(Path.Combine(dir!.FullName, "samples", "offers.sample.csv")));

        Assert.Equal(0, s.Rejected);
        Assert.True(s.Added >= 30);
        Assert.Equal(s.Added, s.Matching.TotalMatched);
        using var db = _testDb.NewContext();
        Assert.Equal(0, db.SourceOffers.Count(o => o.ProductId == null));
    }
}
