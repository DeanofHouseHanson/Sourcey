using BuyList.Domain.Entities;
using BuyList.Infrastructure.Import;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Tests.Import;

public sealed class ProductMatcherTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private int _n;
    public void Dispose() => _testDb.Dispose();

    private SourceOffer Offer(BuyListDbContext db, string? ean = null, string? brand = null, string? mpn = null, string? title = "A title")
    {
        var o = new SourceOffer
        {
            RetailerName = "Shop", SourceUrl = $"https://shop.example/p/{++_n}", BuyPriceExVat = 5, BuyPriceIncVat = 6,
            CheckedAt = DateTime.UtcNow, RawEan = ean, RawBrand = brand, RawMpn = mpn, RawTitle = title
        };
        db.SourceOffers.Add(o);
        return o;
    }

    private async Task<global::BuyList.Application.Import.MatchSummary> Match()
    {
        using var db = _testDb.NewContext();
        return await new ProductMatcher(db).MatchPendingAsync();
    }

    [Fact]
    public async Task Matches_an_existing_product_by_ean()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Asin = "B000000001", Ean = "5012345678900" });
            Offer(db, ean: "5012345678900");
            db.SaveChanges();
        }
        var s = await Match();

        using var read = _testDb.NewContext();
        Assert.Equal(1, s.MatchedByEan);
        Assert.Equal(0, s.ProductsCreated);
        Assert.Equal(read.Products.Single().Id, read.SourceOffers.Single().ProductId);
    }

    [Fact]
    public async Task Upc_offer_matches_a_product_stored_as_ean13()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Ean = "0036000291452" });
            Offer(db, ean: "036000291452");   // same item, UPC-A spelling
            db.SaveChanges();
        }
        Assert.Equal(1, (await Match()).MatchedByEan);
    }

    [Fact]
    public async Task Unknown_valid_ean_creates_a_stub_product_awaiting_keepa()
    {
        using (var db = _testDb.NewContext()) { Offer(db, ean: "5012345678900", brand: "Acme", mpn: "M-1", title: "Widget"); db.SaveChanges(); }
        var s = await Match();

        using var read = _testDb.NewContext();
        var p = read.Products.Single();
        Assert.Equal(1, s.ProductsCreated);
        Assert.Null(p.Asin);
        Assert.Equal("5012345678900", p.Ean);
        Assert.Equal("Acme", p.Brand);
        Assert.Equal("Widget", p.Title);
        Assert.Equal(p.Id, read.SourceOffers.Single().ProductId);
    }

    [Fact]
    public async Task Two_offers_for_one_new_ean_share_one_product()
    {
        using (var db = _testDb.NewContext())
        {
            Offer(db, ean: "5012345678900"); Offer(db, ean: "5012345678900"); db.SaveChanges();
        }
        var s = await Match();
        using var read = _testDb.NewContext();
        Assert.Single(read.Products);
        Assert.Equal(1, s.ProductsCreated);
        Assert.Equal(1, s.MatchedByEan);
    }

    [Fact]
    public async Task Brand_and_mpn_match_is_case_insensitive_and_trims()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Asin = "B000000002", Brand = "Acme", Mpn = "ab-12" });
            Offer(db, brand: " ACME ", mpn: "AB-12 ");
            db.SaveChanges();
        }
        var s = await Match();
        Assert.Equal(1, s.MatchedByBrandMpn);
        using var read = _testDb.NewContext();
        Assert.NotNull(read.SourceOffers.Single().ProductId);
    }

    [Fact]
    public async Task Title_alone_never_matches()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Asin = "B000000003", Title = "Stainless Steel Bottle 750ml" });
            Offer(db, title: "Stainless Steel Bottle 750ml");   // identical title, no identifiers
            db.SaveChanges();
        }
        var s = await Match();
        Assert.Equal(1, s.Unmatchable);
        Assert.Equal(0, s.TotalMatched);
        using var read = _testDb.NewContext();
        Assert.Null(read.SourceOffers.Single().ProductId);
    }

    [Fact]
    public async Task Brand_without_mpn_does_not_match()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Brand = "Acme", Mpn = "M-1" });
            Offer(db, brand: "Acme");
            db.SaveChanges();
        }
        Assert.Equal(1, (await Match()).Unmatchable);
    }

    [Fact]
    public async Task Ambiguous_brand_and_mpn_is_skipped()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.AddRange(new Product { Asin = "B000000004", Brand = "Acme", Mpn = "M-1" },
                                 new Product { Asin = "B000000005", Brand = "Acme", Mpn = "M-1" });
            Offer(db, brand: "Acme", mpn: "M-1");
            db.SaveChanges();
        }
        var s = await Match();
        Assert.Equal(1, s.AmbiguousSkipped);
        using var read = _testDb.NewContext();
        Assert.Null(read.SourceOffers.Single().ProductId);
    }

    [Fact]
    public async Task Brand_and_mpn_only_with_no_existing_product_stays_unmatched_and_creates_nothing()
    {
        using (var db = _testDb.NewContext()) { Offer(db, brand: "Acme", mpn: "M-1"); db.SaveChanges(); }
        var s = await Match();
        Assert.Equal(1, s.NoProductFound);
        using var read = _testDb.NewContext();
        Assert.Empty(read.Products);
    }

    [Fact]
    public async Task Ean_match_wins_over_brand_and_mpn()
    {
        int byEanId;
        using (var db = _testDb.NewContext())
        {
            var byEan = new Product { Asin = "B000000006", Ean = "5012345678900", Brand = "Other", Mpn = "X" };
            db.Products.AddRange(byEan, new Product { Asin = "B000000007", Brand = "Acme", Mpn = "M-1" });
            Offer(db, ean: "5012345678900", brand: "Acme", mpn: "M-1");
            db.SaveChanges();
            byEanId = byEan.Id;
        }
        await Match();
        using var read = _testDb.NewContext();
        Assert.Equal(byEanId, read.SourceOffers.Single().ProductId);
    }

    [Fact]
    public async Task Brand_mpn_match_learns_a_missing_ean()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Asin = "B000000008", Brand = "Acme", Mpn = "M-1" });
            Offer(db, ean: "5012345678900", brand: "Acme", mpn: "M-1");
            db.SaveChanges();
        }
        var s = await Match();
        using var read = _testDb.NewContext();
        Assert.Equal(1, s.MatchedByBrandMpn);
        Assert.Equal("5012345678900", read.Products.Single().Ean);
    }

    [Fact]
    public async Task Same_brand_and_mpn_but_a_different_ean_is_a_different_product()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Asin = "B000000009", Ean = "96385074", Brand = "Acme", Mpn = "M-1" });
            Offer(db, ean: "5012345678900", brand: "Acme", mpn: "M-1");   // e.g. a colour variant sharing an MPN
            db.SaveChanges();
        }
        var s = await Match();
        using var read = _testDb.NewContext();
        Assert.Equal(2, read.Products.Count());
        Assert.Equal(1, s.ProductsCreated);
        Assert.Equal(0, s.MatchedByBrandMpn);
    }

    [Fact]
    public async Task Offers_with_no_identifiers_are_counted_unmatchable()
    {
        using (var db = _testDb.NewContext()) { Offer(db); db.SaveChanges(); }
        Assert.Equal(1, (await Match()).Unmatchable);
    }

    [Fact]
    public async Task Already_matched_offers_are_left_alone_and_reruns_are_idempotent()
    {
        using (var db = _testDb.NewContext()) { Offer(db, ean: "5012345678900"); db.SaveChanges(); }
        await Match();
        var again = await Match();
        using var read = _testDb.NewContext();
        Assert.Equal(0, again.TotalMatched);
        Assert.Single(read.Products);
    }

    [Fact]
    public async Task An_invalid_stored_ean_is_not_used_for_matching()
    {
        using (var db = _testDb.NewContext())
        {
            db.Products.Add(new Product { Ean = "5012345678901" });
            Offer(db, ean: "5012345678901");    // bad check digit
            db.SaveChanges();
        }
        var s = await Match();
        Assert.Equal(0, s.TotalMatched);
    }
}
