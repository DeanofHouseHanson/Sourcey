using BuyList.Application.Options;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Enrichment;
using BuyList.Infrastructure.Keepa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static BuyList.Tests.Enrichment.ScriptedKeepa;

namespace BuyList.Tests.Enrichment;

public sealed class EnrichmentServiceTests : IDisposable
{
    private const string Ean1 = "5012345678900";
    private const string Ean2 = "0036000291452";

    private readonly TestDb _testDb = new();
    private readonly FixedTime _time = new();
    private readonly ScriptedKeepa _keepa = new();
    private KeepaOptions _opts = new() { MaxTokensPerRun = 100, RefreshAgeHours = 24, UnresolvedRetryDays = 7, MinTokensReserve = 20 };
    public void Dispose() => _testDb.Dispose();

    private Task<global::BuyList.Application.Enrichment.EnrichmentSummary> Run()
    {
        var db = _testDb.NewContext();
        return new EnrichmentService(db, _keepa, Options.Create(_opts), _time, NullLogger<EnrichmentService>.Instance).EnrichAsync();
    }

    // ---- resolving stubs ----
    [Fact]
    public async Task Resolving_an_ean_saves_the_asin_and_the_snapshot_from_the_same_response()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1);
        _keepa.Catalogue.Add(Kp("B00RESOLVE", Ean1));

        var s = await Run();

        Assert.Equal(1, s.AsinsResolved);
        Assert.Equal(3, s.TokensUsed);
        Assert.Single(_keepa.Calls);                  // one code lookup, no follow-up ASIN fetch: tokens saved
        using var read = _testDb.NewContext();
        var p = read.Products.Single();
        Assert.Equal("B00RESOLVE", p.Asin);
        Assert.Equal(AsinLookupStatus.Resolved, p.AsinLookupStatus);
        Assert.Equal("Amazon Title", p.Title);
        Assert.Equal(_time.Now.UtcDateTime, p.KeepaCheckedAt);
        var snap = read.AmazonSnapshots.Single();
        Assert.Equal(24.00m, snap.Avg90BuyBox);
        Assert.Equal(60, snap.MonthlySales);
        Assert.Equal(2, snap.FbaSellerCount);
        Assert.Equal(420, snap.WeightGrams);
        Assert.Equal(3.00m, snap.KeepaFbaFee);
        Assert.Equal(1234, snap.SalesRank);
        Assert.False(snap.AmazonOnListing);
    }

    [Fact]
    public async Task Stubs_whose_offers_are_all_out_of_stock_are_not_looked_up()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1, StockStatus.OutOfStock);
        var s = await Run();
        Assert.Empty(_keepa.Calls);
        Assert.Equal(0, s.TokensUsed);
    }

    [Fact]
    public async Task Unknown_ean_is_marked_not_found_and_not_retried_inside_the_retry_window()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1);
        var first = await Run();
        Assert.Equal(1, first.CodesNotFound);

        _time.Now = _time.Now.AddDays(6);
        await Run();
        Assert.Single(_keepa.Calls);                  // still within 7 days: no new spend

        _time.Now = _time.Now.AddDays(2);             // 8 days after the first attempt
        await Run();
        Assert.Equal(2, _keepa.Calls.Count);

        using var read = _testDb.NewContext();
        Assert.Equal(AsinLookupStatus.NotFound, read.Products.Single().AsinLookupStatus);
    }

    [Fact]
    public async Task An_ean_with_several_asins_is_left_ambiguous_and_never_auto_picked()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1);
        _keepa.Catalogue.Add(Kp("B00AAAAAAA", Ean1));
        _keepa.Catalogue.Add(Kp("B00BBBBBBB", Ean1));

        var s = await Run();

        Assert.Equal(1, s.CodesAmbiguous);
        using var read = _testDb.NewContext();
        var p = read.Products.Single();
        Assert.Null(p.Asin);
        Assert.Equal(AsinLookupStatus.Ambiguous, p.AsinLookupStatus);
        Assert.Empty(read.AmazonSnapshots);
    }

    [Fact]
    public async Task A_stub_for_an_asin_we_already_hold_is_merged_into_the_existing_product()
    {
        int existingId;
        using (var db = _testDb.NewContext())
        {
            var existing = Seed.Stub(db, "96385074", asin: "B00SAMEASIN", checkedAt: _time.Now.UtcDateTime);   // fresh: not refreshed
            existingId = existing.Id;
            Seed.Stub(db, Ean1);
        }
        _keepa.Catalogue.Add(Kp("B00SAMEASIN", Ean1));

        await Run();

        using var read = _testDb.NewContext();
        var only = read.Products.Single();
        Assert.Equal(existingId, only.Id);
        Assert.Equal(2, read.SourceOffers.Count(o => o.ProductId == existingId));   // both offers now share one product
    }

    // ---- refreshing ----
    [Fact]
    public async Task Products_with_an_asin_and_no_data_are_fetched_by_asin()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1, asin: "B00REFRESH1");
        _keepa.Catalogue.Add(Kp("B00REFRESH1", Ean1));

        var s = await Run();

        Assert.Equal(1, s.ProductsRefreshed);
        Assert.Equal("asin", _keepa.Calls.Single().Kind);
        using var read = _testDb.NewContext();
        Assert.Single(read.AmazonSnapshots);
    }

    [Fact]
    public async Task Fresh_data_is_not_fetched_again_but_stale_data_is()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1, asin: "B00REFRESH1", checkedAt: _time.Now.UtcDateTime.AddHours(-23));
        _keepa.Catalogue.Add(Kp("B00REFRESH1", Ean1));

        await Run();
        Assert.Empty(_keepa.Calls);                    // 23h old, limit is 24h

        _time.Now = _time.Now.AddHours(2);             // now 25h old
        await Run();
        Assert.Single(_keepa.Calls);
    }

    [Fact]
    public async Task Never_fetched_products_go_before_stale_ones_and_oldest_stale_first()
    {
        using (var db = _testDb.NewContext())
        {
            Seed.Stub(db, "5012345678900", asin: "B00STALEONE", checkedAt: _time.Now.UtcDateTime.AddDays(-2));
            Seed.Stub(db, "96385074", asin: "B00NEVERONE");
            Seed.Stub(db, "0036000291452", asin: "B00STALEOLD", checkedAt: _time.Now.UtcDateTime.AddDays(-9));
        }
        await Run();
        Assert.Equal(new[] { "B00NEVERONE", "B00STALEOLD", "B00STALEONE" }, _keepa.Calls.Single().Items);
    }

    [Fact]
    public async Task Asked_but_not_returned_products_are_not_re_requested_until_stale()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1, asin: "B00GONE0000");
        await Run();                                    // catalogue is empty: Keepa returns nothing
        await Run();
        Assert.Single(_keepa.Calls);
        using var read = _testDb.NewContext();
        Assert.NotNull(read.Products.Single().KeepaCheckedAt);
        Assert.Empty(read.AmazonSnapshots);
    }

    [Fact]
    public async Task Fake_asins_are_never_sent_to_the_live_client()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1, asin: FakeKeepaClient.AsinPrefix + "0001");
        await Run();
        Assert.Empty(_keepa.Calls);
    }

    // ---- budget ----
    [Fact]
    public async Task The_per_run_cap_is_passed_down_and_shared_between_the_two_stages()
    {
        _opts = new KeepaOptions { MaxTokensPerRun = 12, RefreshAgeHours = 24 };
        using (var db = _testDb.NewContext())
        {
            Seed.Stub(db, Ean1);                                 // needs a code lookup (3 tokens)
            Seed.Stub(db, "96385074", asin: "B00REFRESH1");      // needs a refresh
        }
        _keepa.Catalogue.Add(Kp("B00LOOKUP00", Ean1));
        _keepa.Catalogue.Add(Kp("B00REFRESH1", "96385074"));

        var s = await Run();

        Assert.Equal(12, _keepa.Calls[0].MaxTokens);             // full cap for the first stage
        Assert.Equal(9, _keepa.Calls[1].MaxTokens);              // cap minus the 3 already spent
        Assert.Equal(6, s.TokensUsed);
    }

    [Fact]
    public async Task Items_beyond_the_budget_are_deferred_not_dropped_and_not_marked_checked()
    {
        _opts = new KeepaOptions { MaxTokensPerRun = 6, RefreshAgeHours = 24 };    // 2 items at 3 tokens
        using (var db = _testDb.NewContext())
            foreach (var (asin, ean) in new[] { ("B00ITEM0001", "5012345678900"), ("B00ITEM0002", "96385074"), ("B00ITEM0003", "0036000291452") })
                Seed.Stub(db, ean, asin: asin);
        foreach (var (asin, ean) in new[] { ("B00ITEM0001", "5012345678900"), ("B00ITEM0002", "96385074"), ("B00ITEM0003", "0036000291452") })
            _keepa.Catalogue.Add(Kp(asin, ean));

        var s = await Run();

        Assert.Equal(2, s.ProductsRefreshed);
        Assert.Equal(1, s.ProductsDeferred);
        Assert.Equal(6, s.TokensUsed);
        using (var read = _testDb.NewContext())
            Assert.Single(read.Products.Where(p => p.KeepaCheckedAt == null));      // the deferred one is still due

        _opts = new KeepaOptions { MaxTokensPerRun = 6, RefreshAgeHours = 24 };
        var next = await Run();                                                       // next run picks it up
        Assert.Equal(1, next.ProductsRefreshed);
        Assert.Equal(0, next.ProductsDeferred);
    }

    [Fact]
    public async Task A_throttled_lookup_skips_the_refresh_stage()
    {
        using (var db = _testDb.NewContext())
        {
            Seed.Stub(db, Ean1);
            Seed.Stub(db, "96385074", asin: "B00REFRESH1");
        }
        _keepa.Throttle = true;
        var s = await Run();
        Assert.True(s.Throttled);
        Assert.Single(_keepa.Calls);                   // no second request after being throttled
    }

    [Fact]
    public async Task A_bad_key_fails_on_the_free_status_check_before_any_spending()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1);
        _keepa.FailStatusWith = new KeepaException("Keepa says payment is required (402)", 402);
        await Assert.ThrowsAsync<KeepaException>(Run);
        Assert.Empty(_keepa.Calls);
    }

    // ---- snapshot mapping ----
    [Fact]
    public async Task Unknown_fba_count_is_stored_as_zero_but_flagged_approximate()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1, asin: "B00REFRESH1");
        _keepa.Catalogue.Add(Kp("B00REFRESH1", Ean1, fba: null));
        await Run();
        using var read = _testDb.NewContext();
        var snap = read.AmazonSnapshots.Single();
        Assert.Equal(0, snap.FbaSellerCount);
        Assert.True(snap.FbaSellerCountIsApprox);
    }

    [Fact]
    public async Task Missing_sales_and_price_are_stored_as_null_never_invented()
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean1, asin: "B00REFRESH1");
        _keepa.Catalogue.Add(Kp("B00REFRESH1", Ean1, price: null, sold: null));
        await Run();
        using var read = _testDb.NewContext();
        var snap = read.AmazonSnapshots.Single();
        Assert.Null(snap.Avg90BuyBox);
        Assert.Null(snap.MonthlySales);
    }

    [Fact]
    public async Task Amazons_title_replaces_the_supplier_title_but_existing_brand_is_kept()
    {
        using (var db = _testDb.NewContext())
        {
            var p = Seed.Stub(db, Ean1, asin: "B00REFRESH1");
            p.Brand = "My Brand";
            db.SaveChanges();
        }
        _keepa.Catalogue.Add(Kp("B00REFRESH1", Ean1));
        await Run();
        using var read = _testDb.NewContext();
        var product = read.Products.Single();
        Assert.Equal("Amazon Title", product.Title);
        Assert.Equal("My Brand", product.Brand);
    }
}
