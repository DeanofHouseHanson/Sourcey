using BuyList.Application.Listing;
using BuyList.Application.Options;
using BuyList.Application.Ranking;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Listing;
using BuyList.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Listing;

public sealed class BuyListServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly FixedTime _time = new();   // 2026-03-01 12:00 UTC
    private int _n;
    public void Dispose() => _testDb.Dispose();

    private BuyListService Svc(BuyListDbContext db) =>
        new(db, Options.Create(new RankingOptions { StaleAfterHours = 24 }), _time);

    /// <summary>Inserts one product + offer + snapshot + opportunity with the given characteristics.</summary>
    private int Add(string name, decimal profit = 5m, decimal roi = 50m, decimal est = 100m, decimal adj = 100m, RiskLevel risk = RiskLevel.Low,
        int? sales = 100, bool amazon = false, OpportunityFlags flags = OpportunityFlags.None, OpportunityStatus status = OpportunityStatus.New,
        int hoursOld = 1, string retailer = "Shop", decimal buyInc = 10m, decimal? days = 10m, int fba = 1, int qty = 5, decimal maxBuy = 12m, decimal? avg = 24m)
    {
        using var db = _testDb.NewContext();
        var p = new Product { Asin = $"B00{++_n:D7}", Title = name, Brand = "Acme" };
        var offer = new SourceOffer
        {
            Product = p, RetailerName = retailer, SourceUrl = $"https://shop.example/{_n}", BuyPriceExVat = buyInc / 1.2m, BuyPriceIncVat = buyInc,
            CheckedAt = _time.Now.UtcDateTime.AddHours(-hoursOld), StockStatus = StockStatus.InStock
        };
        var snap = new AmazonSnapshot
        {
            Product = p, CapturedAt = _time.Now.UtcDateTime.AddHours(-1), Avg90BuyBox = avg, MonthlySales = sales, FbaSellerCount = fba, AmazonOnListing = amazon
        };
        var opp = new Opportunity
        {
            Product = p, SourceOffer = offer, NetProfit = profit, RoiPercent = roi, MarginPercent = 20m, MaxBuyPrice = maxBuy, EstMonthlyProfit = est,
            RiskLevel = risk, RiskAdjustedMonthlyProfit = adj, DaysToSell = days, SuggestedQty = qty, Status = status, Flags = flags,
            CalculatedAt = _time.Now.UtcDateTime
        };
        db.AddRange(p, offer, snap, opp);
        db.SaveChanges();
        return opp.Id;
    }

    private async Task<string[]> Names(BuyListQuery? q = null)
    {
        using var db = _testDb.NewContext();
        return (await Svc(db).QueryAsync(q ?? new BuyListQuery())).Rows.Select(r => r.Title!).ToArray();
    }

    // ---------------- default view ----------------
    [Fact]
    public async Task Default_view_is_ranked_by_risk_adjusted_monthly_profit_descending()
    {
        Add("low", adj: 10); Add("high", adj: 300); Add("mid", adj: 120);
        Assert.Equal(new[] { "high", "mid", "low" }, await Names());
    }

    [Fact]
    public async Task Default_view_hides_loss_making_amazon_stale_ignored_bought_and_blocklisted()
    {
        Add("keep");
        Add("loss", profit: -1m);
        Add("amazon", amazon: true);
        Add("stale", hoursOld: 25);
        Add("ignored", status: OpportunityStatus.Ignored);
        Add("bought", status: OpportunityStatus.Bought);
        Add("blocked", flags: OpportunityFlags.Blocklisted);
        Assert.Equal(new[] { "keep" }, await Names());
    }

    [Fact]
    public async Task Checked_rows_are_included_by_default()
    {
        Add("new"); Add("checked", status: OpportunityStatus.Checked);
        Assert.Equal(2, (await Names()).Length);
    }

    [Fact]
    public async Task Zero_profit_is_kept_but_clearing_the_minimum_shows_losses_too()
    {
        Add("zero", profit: 0m); Add("loss", profit: -2m);
        Assert.Equal(new[] { "zero" }, await Names());
        Assert.Equal(2, (await Names(new BuyListQuery { MinNetProfit = null })).Length);
    }

    // ---------------- ranking mode & tie-breaks ----------------
    [Fact]
    public async Task Raw_ranking_orders_by_unadjusted_monthly_profit()
    {
        Add("risky-big", est: 500, adj: 125, risk: RiskLevel.VeryHigh);
        Add("safe-small", est: 200, adj: 200, risk: RiskLevel.Low);
        Assert.Equal(new[] { "safe-small", "risky-big" }, await Names());                                    // risk-adjusted (default)
        Assert.Equal(new[] { "risky-big", "safe-small" }, await Names(new BuyListQuery { Ranking = RankingMode.Raw }));
    }

    [Fact]
    public async Task Ties_break_on_roi_then_profit_then_age()
    {
        Add("e", adj: 50, roi: 40, profit: 1); Add("d", adj: 50, roi: 60, profit: 1);
        Add("c", adj: 50, roi: 40, profit: 3); Add("b", adj: 50, roi: 40, profit: 1);
        Assert.Equal(new[] { "d", "c", "e", "b" }, await Names());   // d best ROI; c more profit; e before b (older row)
    }

    // ---------------- individual filters ----------------
    [Fact]
    public async Task Min_roi() { Add("a", roi: 20); Add("b", roi: 35); Assert.Equal(new[] { "b" }, await Names(new BuyListQuery { MinRoi = 30 })); }

    [Fact]
    public async Task Min_net_profit() { Add("a", profit: 1.5m); Add("b", profit: 4m); Assert.Equal(new[] { "b" }, await Names(new BuyListQuery { MinNetProfit = 2 })); }

    [Fact]
    public async Task Min_monthly_sales_excludes_rows_with_no_sales_data()
    {
        Add("many", sales: 80); Add("few", sales: 5); Add("nodata", sales: null);
        Assert.Equal(new[] { "many" }, await Names(new BuyListQuery { MinMonthlySales = 30 }));
        Assert.Equal(3, (await Names()).Length);   // no minimum: no-data rows are still listed
    }

    [Theory]
    [InlineData(RiskLevel.VeryHigh, 5)]
    [InlineData(RiskLevel.High, 4)]
    [InlineData(RiskLevel.Medium, 3)]
    [InlineData(RiskLevel.Low, 2)]
    [InlineData(RiskLevel.VeryLow, 1)]
    public async Task Max_risk_hides_anything_riskier(RiskLevel max, int expected)
    {
        foreach (var level in Enum.GetValues<RiskLevel>()) Add(level.ToString(), risk: level);
        var names = await Names(new BuyListQuery { MaxRisk = max });
        Assert.Equal(expected, names.Length);
        Assert.All(names, n => Assert.True(Enum.Parse<RiskLevel>(n) >= max));
    }

    [Fact]
    public async Task Amazon_on_listing_can_be_shown()
    {
        Add("plain"); Add("amazon", amazon: true);
        Assert.Equal(2, (await Names(new BuyListQuery { HideAmazon = false })).Length);
    }

    [Fact]
    public async Task Hide_flagged_covers_eligibility_flags_but_not_missing_sales_data()
    {
        Add("clean"); Add("gated", flags: OpportunityFlags.Gated); Add("hazmat", flags: OpportunityFlags.Hazmat);
        Add("ip", flags: OpportunityFlags.IpComplaintProne); Add("nodata", flags: OpportunityFlags.NoSalesData, sales: null);
        Assert.Equal(5, (await Names()).Length);
        Assert.Equal(new[] { "clean", "nodata" }.OrderBy(x => x), (await Names(new BuyListQuery { HideFlagged = true })).OrderBy(x => x));
    }

    [Fact]
    public async Task Max_unit_cost_uses_the_price_at_the_till()
    {
        Add("cheap", buyInc: 9.99m); Add("exact", buyInc: 10m); Add("dear", buyInc: 10.01m);
        Assert.Equal(new[] { "cheap", "exact" }.OrderBy(x => x), (await Names(new BuyListQuery { MaxUnitCost = 10 })).OrderBy(x => x));
    }

    [Fact]
    public async Task Retailer_filter() { Add("a", retailer: "Alpha"); Add("b", retailer: "Beta"); Assert.Equal(new[] { "b" }, await Names(new BuyListQuery { Retailer = "Beta" })); }

    [Fact]
    public async Task Stale_rows_appear_only_when_asked_and_are_marked_stale()
    {
        Add("fresh", hoursOld: 23); Add("stale", hoursOld: 25, adj: 1);
        Assert.Equal(new[] { "fresh" }, await Names());

        using var db = _testDb.NewContext();
        var r = (await Svc(db).QueryAsync(new BuyListQuery { IncludeStale = true })).Rows;
        Assert.False(r.Single(x => x.Title == "fresh").IsStale);
        Assert.True(r.Single(x => x.Title == "stale").IsStale);
    }

    [Fact]
    public async Task Status_filter_can_surface_ignored_rows()
    {
        Add("new"); Add("ignored", status: OpportunityStatus.Ignored); Add("checked", status: OpportunityStatus.Checked);
        Assert.Equal(new[] { "ignored" }, await Names(new BuyListQuery { Status = OpportunityStatus.Ignored }));
        Assert.Equal(new[] { "checked" }, await Names(new BuyListQuery { Status = OpportunityStatus.Checked }));
    }

    [Fact]
    public async Task Bought_view_shows_only_bought_rows_with_no_default_exclusions()
    {
        Add("bought-ok", status: OpportunityStatus.Bought);
        Add("bought-loss-stale-amazon", status: OpportunityStatus.Bought, profit: -3m, hoursOld: 99, amazon: true);
        Add("new");
        var names = await Names(new BuyListQuery { View = BuyListView.Bought });
        Assert.Equal(2, names.Length);
        Assert.DoesNotContain("new", names);
    }

    // ---------------- sorting ----------------
    [Theory]
    [InlineData(BuyListSort.Profit, false, new[] { "p1", "p2", "p3" })]
    [InlineData(BuyListSort.Profit, true, new[] { "p3", "p2", "p1" })]
    public async Task Sorting_by_profit_either_way(BuyListSort sort, bool desc, string[] expected)
    {
        Add("p2", profit: 2); Add("p3", profit: 3); Add("p1", profit: 1);
        Assert.Equal(expected, await Names(new BuyListQuery { Sort = sort, Desc = desc }));
    }

    [Fact]
    public async Task Sorting_by_risk_goes_from_riskiest_to_safest_not_alphabetically()
    {
        foreach (var l in new[] { RiskLevel.Low, RiskLevel.VeryHigh, RiskLevel.VeryLow, RiskLevel.High, RiskLevel.Medium }) Add(l.ToString(), risk: l);
        Assert.Equal(new[] { "VeryHigh", "High", "Medium", "Low", "VeryLow" }, await Names(new BuyListQuery { Sort = BuyListSort.Risk, Desc = false }));
        Assert.Equal(new[] { "VeryLow", "Low", "Medium", "High", "VeryHigh" }, await Names(new BuyListQuery { Sort = BuyListSort.Risk, Desc = true }));
    }

    [Theory]
    [InlineData(BuyListSort.Title)] [InlineData(BuyListSort.Buy)] [InlineData(BuyListSort.Retailer)] [InlineData(BuyListSort.Checked)]
    [InlineData(BuyListSort.Avg)] [InlineData(BuyListSort.Profit)] [InlineData(BuyListSort.Roi)] [InlineData(BuyListSort.Sales)]
    [InlineData(BuyListSort.Monthly)] [InlineData(BuyListSort.Adjusted)] [InlineData(BuyListSort.Risk)] [InlineData(BuyListSort.Days)]
    [InlineData(BuyListSort.Fba)] [InlineData(BuyListSort.MaxBuy)] [InlineData(BuyListSort.Qty)] [InlineData(BuyListSort.Status)] [InlineData(BuyListSort.Default)]
    public async Task Every_sortable_column_runs_in_both_directions(BuyListSort sort)
    {
        Add("a", sales: null, days: null); Add("b"); Add("c", avg: null);
        foreach (var desc in new[] { true, false })
            Assert.Equal(3, (await Names(new BuyListQuery { Sort = sort, Desc = desc })).Length);
    }

    [Fact]
    public async Task Sorting_by_sales_puts_no_data_rows_at_the_low_end()
    {
        Add("none", sales: null); Add("low", sales: 10); Add("high", sales: 500);
        Assert.Equal(new[] { "none", "low", "high" }, await Names(new BuyListQuery { Sort = BuyListSort.Sales, Desc = false }));
    }

    // ---------------- paging ----------------
    [Fact]
    public async Task Paging_returns_pages_and_totals()
    {
        for (var i = 1; i <= 7; i++) Add($"item{i}", adj: 100 - i);
        using var db = _testDb.NewContext();
        var page2 = await Svc(db).QueryAsync(new BuyListQuery { PageSize = 3, Page = 2 });
        Assert.Equal(new[] { "item4", "item5", "item6" }, page2.Rows.Select(r => r.Title));
        Assert.Equal(7, page2.TotalRows);
        Assert.Equal(3, page2.TotalPages);
    }

    [Fact]
    public async Task A_page_beyond_the_end_is_clamped_to_the_last_page()
    {
        for (var i = 1; i <= 4; i++) Add($"item{i}", adj: 100 - i);
        using var db = _testDb.NewContext();
        var r = await Svc(db).QueryAsync(new BuyListQuery { PageSize = 3, Page = 99 });
        Assert.Equal(2, r.Page);
        Assert.Equal(new[] { "item4" }, r.Rows.Select(x => x.Title));
    }

    [Fact]
    public void Silly_paging_values_are_made_safe()
    {
        var q = new BuyListQuery { Page = -4, PageSize = 100_000 };
        Assert.Equal(1, q.SafePage);
        Assert.Equal(BuyListQuery.MaxPageSize, q.SafePageSize);
        Assert.Equal(1, new BuyListQuery { PageSize = 0 }.SafePageSize);
    }

    // ---------------- data shown per row ----------------
    [Fact]
    public async Task Rows_carry_the_latest_snapshot_not_an_older_one()
    {
        var id = Add("p", sales: 50, fba: 2, avg: 24m);
        using (var db = _testDb.NewContext())
        {
            var productId = db.Opportunities.Single(o => o.Id == id).ProductId;
            db.AmazonSnapshots.Add(new AmazonSnapshot { ProductId = productId, CapturedAt = _time.Now.UtcDateTime.AddDays(-10), Avg90BuyBox = 99m, MonthlySales = 999, FbaSellerCount = 9 });
            db.SaveChanges();
        }
        using var read = _testDb.NewContext();
        var row = (await Svc(read).QueryAsync(new BuyListQuery())).Rows.Single();
        Assert.Equal(24m, row.Avg90BuyBox);
        Assert.Equal(50, row.MonthlySales);
        Assert.Equal(2, row.FbaSellerCount);
    }

    [Fact]
    public async Task Every_row_has_a_source_link_and_a_retailer()
    {
        Add("a", retailer: "Alpha"); Add("b", retailer: "Beta");
        using var db = _testDb.NewContext();
        var rows = (await Svc(db).QueryAsync(new BuyListQuery())).Rows;
        Assert.All(rows, r => { Assert.StartsWith("https://", r.SourceUrl); Assert.NotEmpty(r.Retailer); });
    }

    [Fact]
    public async Task Missing_values_come_through_as_null_for_the_page_to_label_no_data()
    {
        Add("x", sales: null, days: null, avg: 20m);
        using var db = _testDb.NewContext();
        var row = (await Svc(db).QueryAsync(new BuyListQuery())).Rows.Single();
        Assert.Null(row.MonthlySales);
        Assert.Null(row.DaysToSell);
    }

    [Fact]
    public async Task Retailer_options_are_listed_alphabetically_and_distinct()
    {
        Add("a", retailer: "Zed"); Add("b", retailer: "Alpha"); Add("c", retailer: "Zed");
        using var db = _testDb.NewContext();
        Assert.Equal(new[] { "Alpha", "Zed" }, (await Svc(db).QueryAsync(new BuyListQuery())).Retailers);
    }

    // ---------------- explaining what's hidden ----------------
    [Fact]
    public async Task Hidden_summary_counts_what_each_default_filter_is_hiding()
    {
        Add("keep");
        Add("loss1", profit: -1m); Add("loss2", profit: -2m);
        Add("amazon", amazon: true);
        Add("stale", hoursOld: 30);
        Add("ignored", status: OpportunityStatus.Ignored);
        Add("blocked", flags: OpportunityFlags.Blocklisted);
        Add("bought", status: OpportunityStatus.Bought, profit: -5m);   // bought rows are never counted as "hidden"

        using var db = _testDb.NewContext();
        var h = (await Svc(db).QueryAsync(new BuyListQuery())).Hidden;
        Assert.Equal(2, h.LossMaking);
        Assert.Equal(1, h.AmazonOnListing);
        Assert.Equal(1, h.Stale);
        Assert.Equal(1, h.Ignored);
        Assert.Equal(1, h.Blocklisted);
    }

    [Fact]
    public async Task Hidden_summary_describes_only_the_current_view_including_the_retailer_filter()
    {
        Add("a-amazon", amazon: true, retailer: "Alpha"); Add("b-amazon", amazon: true, retailer: "Beta"); Add("b-loss", profit: -1m, retailer: "Beta");
        using var db = _testDb.NewContext();
        var h = (await Svc(db).QueryAsync(new BuyListQuery { Retailer = "Alpha" })).Hidden;
        Assert.Equal(1, h.AmazonOnListing);     // not 2: Beta's rows aren't part of this view
        Assert.Equal(0, h.LossMaking);
    }

    [Fact]
    public async Task Hidden_summary_ignores_filters_the_user_has_switched_off()
    {
        Add("amazon", amazon: true); Add("stale", hoursOld: 30);
        using var db = _testDb.NewContext();
        var h = (await Svc(db).QueryAsync(new BuyListQuery { HideAmazon = false, IncludeStale = true })).Hidden;
        Assert.Equal(0, h.AmazonOnListing);
        Assert.Equal(0, h.Stale);
    }

    // ---------------- actions & export ----------------
    [Fact]
    public async Task Setting_status_persists_and_ignored_rows_leave_the_list()
    {
        var id = Add("x");
        using (var db = _testDb.NewContext()) Assert.True(await Svc(db).SetStatusAsync(id, OpportunityStatus.Ignored));
        Assert.Empty(await Names());
        using (var db = _testDb.NewContext()) Assert.True(await Svc(db).SetStatusAsync(id, OpportunityStatus.New));
        Assert.Single(await Names());
    }

    [Fact]
    public async Task Setting_status_on_an_unknown_row_reports_false()
    {
        using var db = _testDb.NewContext();
        Assert.False(await Svc(db).SetStatusAsync(12345, OpportunityStatus.Bought));
    }

    [Fact]
    public async Task Query_all_ignores_paging_and_respects_filters_and_the_cap()
    {
        for (var i = 1; i <= 6; i++) Add($"item{i}", adj: i);
        Add("loss", profit: -1m);
        using var db = _testDb.NewContext();
        var all = await Svc(db).QueryAllAsync(new BuyListQuery { PageSize = 2, Page = 2 });
        Assert.Equal(6, all.Count);
        Assert.Equal("item6", all[0].Title);
        Assert.Equal(3, (await Svc(db).QueryAllAsync(new BuyListQuery(), max: 3)).Count);
    }

    // ---------------- pipeline status ----------------
    [Fact]
    public async Task Pipeline_status_counts_each_stage()
    {
        Add("has-opp");
        using (var db = _testDb.NewContext())
        {
            db.Products.AddRange(
                new Product { Ean = "5012345678900" },                                                   // waiting for a lookup
                new Product { Ean = "96385074", AsinLookupStatus = AsinLookupStatus.Ambiguous },
                new Product { Ean = "0036000291452", AsinLookupStatus = AsinLookupStatus.NotFound });
            var noPrice = new Product { Asin = "B00NOPRICE0" };
            noPrice.Snapshots.Add(new AmazonSnapshot { CapturedAt = _time.Now.UtcDateTime, Avg90BuyBox = null });
            db.Products.Add(noPrice);
            db.SaveChanges();
        }
        using var read = _testDb.NewContext();
        var s = await Svc(read).GetPipelineStatusAsync();
        Assert.Equal(1, s.Offers);
        Assert.Equal(1, s.OffersMatched);
        Assert.Equal(1, s.ProductsAwaitingLookup);
        Assert.Equal(1, s.ProductsAmbiguous);
        Assert.Equal(1, s.ProductsNotFound);
        Assert.Equal(1, s.ProductsWithoutBuyBoxPrice);
        Assert.Equal(1, s.Opportunities);
    }
}
