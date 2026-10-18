using BuyList.Application.Eligibility;
using BuyList.Application.Options;
using BuyList.Application.Profit;
using BuyList.Application.Ranking;
using BuyList.Application.Risk;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Opportunities;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Enrichment;

public sealed class OpportunityServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly FixedTime _time = new();
    public void Dispose() => _testDb.Dispose();

    private OpportunityService Make(BuyListDbContext db)
    {
        var fees = new ApproximateFeeEstimator(Options.Create(new ProfitOptions()), Options.Create(new FbaFeeOptions
        {
            DefaultWeightGrams = 400, Tiers = { new FbaFeeTier { Name = "500g", MaxWeightGrams = 500, Fee = 2.90m } }
        }));
        return new OpportunityService(db,
            new ProfitCalculator(Options.Create(new ProfitOptions()), fees),
            new OpportunityScorer(Options.Create(new RankingOptions { BudgetPounds = 500 }), new RiskClassifier(Options.Create(new RiskOptions()))),
            new ManualEligibilityChecker(), _time);
    }

    private Task<global::BuyList.Application.Opportunities.RecalcSummary> Recalc()
    {
        var db = _testDb.NewContext();
        return Make(db).RecalculateAsync();
    }

    private static AmazonSnapshot Snap(Product p, decimal? avg = 24.00m, int? sales = 60, int fba = 2, decimal? fee = 3.00m, DateTime? at = null) => new()
    {
        Product = p, CapturedAt = at ?? new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), Avg90BuyBox = avg, MonthlySales = sales,
        FbaSellerCount = fba, KeepaFbaFee = fee
    };

    private (Product Product, SourceOffer Offer) Setup(Action<AmazonSnapshot>? tweak = null, StockStatus stock = StockStatus.InStock)
    {
        using var db = _testDb.NewContext();
        var p = new Product { Asin = "B00TESTAAA", Ean = "5012345678900" };
        var o = Seed.Offer(p, stock);
        var s = Snap(p);
        tweak?.Invoke(s);
        db.AddRange(p, o, s);
        db.SaveChanges();
        return (p, o);
    }

    // Worked example (registered, VAT 20%, default options): sell £24.00, buy £8.00 ex / £9.60 inc, Keepa FBA fee £3.00.
    //   profit = 20.00 - 8.00 - 3.60 - 3.00 - 0.50 - 0.40 = 4.50; ROI 52.94%; margin 22.50%; max buy (30% ROI) £11.40 inc VAT
    //   60 sales/month, 2 FBA sellers => 20 units/month; £500 / £9.60 buys 52 => 20 units
    //   est monthly profit = 4.50 * 20 = 90.00; Medium risk x0.8 = 72.00; qty 20; days to sell 20/20*30 = 30.0
    [Fact]
    public async Task Calculates_the_worked_example_end_to_end()
    {
        Setup();
        var s = await Recalc();

        Assert.Equal(1, s.Created);
        using var db = _testDb.NewContext();
        var o = db.Opportunities.Single();
        Assert.Equal(4.50m, o.NetProfit);
        Assert.Equal(52.94m, o.RoiPercent);
        Assert.Equal(22.50m, o.MarginPercent);
        Assert.Equal(11.40m, o.MaxBuyPrice);
        Assert.Equal(90.00m, o.EstMonthlyProfit);
        Assert.Equal(RiskLevel.Medium, o.RiskLevel);
        Assert.Equal(72.00m, o.RiskAdjustedMonthlyProfit);
        Assert.Equal(20, o.SuggestedQty);
        Assert.Equal(30.0m, o.DaysToSell);
        Assert.Equal(OpportunityStatus.New, o.Status);
        Assert.Equal(OpportunityFlags.None, o.Flags);
        Assert.Equal(_time.Now.UtcDateTime, o.CalculatedAt);
    }

    [Fact]
    public async Task Keepas_fba_fee_is_preferred_over_the_table()
    {
        Setup(s => s.KeepaFbaFee = null);          // falls back to the £2.90 table tier => profit 4.60
        await Recalc();
        using var db = _testDb.NewContext();
        Assert.Equal(4.60m, db.Opportunities.Single().NetProfit);
    }

    [Fact]
    public async Task Missing_sales_data_is_flagged_very_high_risk_and_estimates_nothing()
    {
        Setup(s => s.MonthlySales = null);
        await Recalc();
        using var db = _testDb.NewContext();
        var o = db.Opportunities.Single();
        Assert.True(o.Flags.HasFlag(OpportunityFlags.NoSalesData));
        Assert.Equal(RiskLevel.VeryHigh, o.RiskLevel);
        Assert.Equal(0m, o.EstMonthlyProfit);
        Assert.Equal(0m, o.RiskAdjustedMonthlyProfit);
        Assert.Null(o.DaysToSell);
        Assert.Equal(1, o.SuggestedQty);
    }

    [Fact]
    public async Task No_buy_box_price_means_no_opportunity()
    {
        Setup(s => s.Avg90BuyBox = null);
        var s = await Recalc();
        Assert.Equal(0, s.Created);
        Assert.Equal(1, s.SkippedNoPrice);
    }

    [Fact]
    public async Task Out_of_stock_offers_get_no_opportunity()
    {
        Setup(stock: StockStatus.OutOfStock);
        var s = await Recalc();
        Assert.Equal(1, s.SkippedOutOfStock);
        using var db = _testDb.NewContext();
        Assert.Empty(db.Opportunities);
    }

    [Fact]
    public async Task Products_without_an_asin_get_no_opportunity()
    {
        using (var db = _testDb.NewContext())
        {
            var p = new Product { Ean = "5012345678900" };
            db.AddRange(p, Seed.Offer(p));
            db.SaveChanges();
        }
        Assert.Equal(0, (await Recalc()).Created);
    }

    [Fact]
    public async Task Only_the_latest_snapshot_is_used()
    {
        var (p, _) = Setup();
        using (var db = _testDb.NewContext())
        {
            db.AmazonSnapshots.Add(new AmazonSnapshot
            {
                ProductId = p.Id, CapturedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Avg90BuyBox = 99.00m, MonthlySales = 999, FbaSellerCount = 0
            });
            db.SaveChanges();
        }
        await Recalc();
        using var read = _testDb.NewContext();
        Assert.Equal(4.50m, read.Opportunities.Single().NetProfit);     // from the £24.00 snapshot, not the older £99.00 one
    }

    [Fact]
    public async Task Amazon_on_listing_still_gets_an_opportunity_for_the_page_filters_to_hide()
    {
        Setup(s => s.AmazonOnListing = true);
        Assert.Equal(1, (await Recalc()).Created);
    }

    [Fact]
    public async Task Manual_eligibility_flags_flow_through()
    {
        var (p, _) = Setup();
        using (var db = _testDb.NewContext())
        {
            db.Products.Single().ManualFlags = OpportunityFlags.Gated | OpportunityFlags.Hazmat;
            db.SaveChanges();
        }
        await Recalc();
        using var read = _testDb.NewContext();
        Assert.Equal(OpportunityFlags.Gated | OpportunityFlags.Hazmat, read.Opportunities.Single().Flags);
    }

    [Fact]
    public async Task Each_retailers_offer_gets_its_own_opportunity()
    {
        var (p, _) = Setup();
        using (var db = _testDb.NewContext())
        {
            db.Add(Seed.OfferFor(p.Id, "Other Shop", 7.00m, 8.40m));
            db.SaveChanges();
        }
        var s = await Recalc();
        Assert.Equal(2, s.Created);
    }

    // ---- re-running ----
    [Fact]
    public async Task Rerunning_updates_in_place_and_preserves_the_owners_status()
    {
        var (_, offer) = Setup();
        await Recalc();
        using (var db = _testDb.NewContext())
        {
            db.Opportunities.Single().Status = OpportunityStatus.Bought;
            db.AmazonSnapshots.Single().Avg90BuyBox = 30.00m;       // price moved
            db.SaveChanges();
        }
        _time.Now = _time.Now.AddHours(1);

        var s = await Recalc();

        Assert.Equal(0, s.Created);
        Assert.Equal(1, s.Updated);
        using var read = _testDb.NewContext();
        var o = read.Opportunities.Single();
        Assert.Equal(OpportunityStatus.Bought, o.Status);
        Assert.True(o.NetProfit > 4.50m);
        Assert.Equal(_time.Now.UtcDateTime, o.CalculatedAt);
        Assert.Equal(offer.Id, o.SourceOfferId);
    }

    [Fact]
    public async Task Rows_that_stop_qualifying_are_removed_unless_the_owner_has_acted_on_them()
    {
        Setup();
        await Recalc();
        using (var db = _testDb.NewContext())
        {
            // a second offer, which the owner marks Checked
            var p = db.Products.Single();
            var second = Seed.OfferFor(p.Id, "Second");
            db.Add(second);
            db.SaveChanges();
        }
        await Recalc();
        using (var db = _testDb.NewContext())
        {
            db.Opportunities.OrderByDescending(o => o.Id).First().Status = OpportunityStatus.Checked;
            foreach (var offer in db.SourceOffers) offer.StockStatus = StockStatus.OutOfStock;   // both go out of stock
            db.SaveChanges();
        }

        var s = await Recalc();

        Assert.Equal(1, s.Removed);                    // the untouched New one
        using var read = _testDb.NewContext();
        Assert.Equal(OpportunityStatus.Checked, read.Opportunities.Single().Status);   // the Checked one stays
    }

    [Fact]
    public async Task Second_run_with_no_changes_creates_nothing()
    {
        Setup();
        await Recalc();
        var again = await Recalc();
        Assert.Equal(0, again.Created);
        Assert.Equal(0, again.Removed);
    }
}
