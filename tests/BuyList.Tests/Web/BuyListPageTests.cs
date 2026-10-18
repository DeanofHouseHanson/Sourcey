using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BuyList.Tests.Web;

public sealed class BuyListPageTests : IClassFixture<BuyListPageTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"buylist-page-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:BuyList", $"Data Source={_db}");
            builder.UseSetting("Seed:OnStartup", "false");
        }
    }

    private readonly Factory _factory;
    private static int _n;
    public BuyListPageTests(Factory f) => _factory = f;

    private static string Antiforgery(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<HttpClient> LoggedIn()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await (await c.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Antiforgery(html), ["Username"] = "admin", ["Password"] = "changeme" }));
        return c;
    }

    /// <summary>Adds a distinctively named opportunity and returns its id. Names are unique so tests don't see each other's rows.</summary>
    private int Add(string title, decimal profit = 5m, int? sales = 100, RiskLevel risk = RiskLevel.Low, bool amazon = false,
        OpportunityStatus status = OpportunityStatus.New, string retailer = "TestShop", decimal adj = 100m, int hoursOld = 1,
        OpportunityFlags flags = OpportunityFlags.None, string? url = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuyListDbContext>();
        var k = Interlocked.Increment(ref _n);
        var p = new Product { Asin = $"B0PG{k:D6}", Title = title, Brand = "Acme", Ean = $"50000{k:D8}" };
        var o = new SourceOffer
        {
            Product = p, RetailerName = retailer, SourceUrl = url ?? $"https://{retailer.ToLowerInvariant()}.example/p/{k}", BuyPriceExVat = 8m, BuyPriceIncVat = 9.6m,
            CheckedAt = DateTime.UtcNow.AddHours(-hoursOld), StockStatus = StockStatus.InStock
        };
        var s = new AmazonSnapshot { Product = p, CapturedAt = DateTime.UtcNow, Avg90BuyBox = 24m, MonthlySales = sales, FbaSellerCount = 2, AmazonOnListing = amazon };
        var opp = new Opportunity
        {
            Product = p, SourceOffer = o, NetProfit = profit, RoiPercent = 50m, MarginPercent = 20m, MaxBuyPrice = 11.4m, EstMonthlyProfit = adj,
            RiskLevel = risk, RiskAdjustedMonthlyProfit = adj, DaysToSell = sales is null ? null : 12m, SuggestedQty = 7, Status = status, Flags = flags,
            CalculatedAt = DateTime.UtcNow
        };
        db.AddRange(p, o, s, opp);
        db.SaveChanges();
        return opp.Id;
    }

    private static async Task<string> Html(HttpClient c, string url) => await (await c.GetAsync(url)).Content.ReadAsStringAsync();

    [Fact]
    public async Task Requires_login()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/?handler=Export")).StatusCode);
    }

    [Fact]
    public async Task The_page_uses_the_dark_theme()
    {
        var html = await Html(await LoggedIn(), "/");
        Assert.Contains("data-bs-theme=\"dark\"", html);
        Assert.DoesNotContain("navbar-light", html);
        Assert.DoesNotContain("bg-white", html);
    }

    [Fact]
    public async Task Rows_show_title_retailer_a_safe_source_link_and_when_it_was_checked()
    {
        Add("Zebra Teapot", retailer: "Teashop", url: "https://teashop.example/zebra");
        var html = await Html(await LoggedIn(), "/?Retailer=Teashop");

        Assert.Contains("Zebra Teapot", html);
        Assert.Contains("Teashop", html);
        Assert.Matches(@"<a href=""https://teashop\.example/zebra"" target=""_blank"" rel=""noopener noreferrer"">Teashop</a>", html);
        Assert.Contains("checked 1h ago", html);
    }

    [Fact]
    public async Task Every_external_link_opens_safely()
    {
        Add("Quill Lamp", retailer: "Lampco");
        var html = await Html(await LoggedIn(), "/?Retailer=Lampco");
        var anchors = Regex.Matches(html, "<a [^>]*target=\"_blank\"[^>]*>");
        Assert.True(anchors.Count >= 4);   // Amazon, Keepa, SAS, Source (+ retailer)
        Assert.All(anchors.Select(m => m.Value), a => Assert.Contains("rel=\"noopener noreferrer\"", a));
        Assert.Contains("https://www.amazon.co.uk/dp/B0PG", html);
        Assert.Contains("https://keepa.com/#!product/2-B0PG", html);
        Assert.Contains("sas.selleramp.com/sas/lookup", html);
    }

    [Fact]
    public async Task Missing_sales_data_reads_no_data_and_shows_the_very_high_risk_badge()
    {
        Add("Mystery Gadget", sales: null, risk: RiskLevel.VeryHigh, retailer: "Mystery");
        var html = await Html(await LoggedIn(), "/?Retailer=Mystery");
        var row = html.Substring(html.IndexOf("Mystery Gadget", StringComparison.Ordinal));
        row = row.Substring(0, row.IndexOf("</tr>", StringComparison.Ordinal));

        Assert.True(Regex.Matches(row, "no data").Count >= 4, "sales, est £/mo, risk-adj £/mo and days to sell should all say no data");
        Assert.Contains("risk risk-VeryHigh", row);
        Assert.Contains("Very high", row);
        Assert.DoesNotContain("N/A", row);
    }

    [Fact]
    public async Task Risk_badges_have_a_class_and_text_for_every_level()
    {
        foreach (var l in Enum.GetValues<RiskLevel>()) Add("Risk " + l, risk: l, retailer: "RiskShop");
        var html = await Html(await LoggedIn(), "/?Retailer=RiskShop");
        foreach (var l in Enum.GetValues<RiskLevel>()) Assert.Contains($"risk risk-{l}", html);
        foreach (var label in new[] { "Very high", "High", "Medium", "Low", "Very low" }) Assert.Contains(">" + label + "<", html);
    }

    [Fact]
    public async Task Default_filters_hide_amazon_loss_making_and_ignored_rows_but_the_page_says_so()
    {
        Add("Visible Item", retailer: "Filtered");
        Add("Amazon Item", amazon: true, retailer: "Filtered");
        Add("Loss Item", profit: -2m, retailer: "Filtered");
        Add("Ignored Item", status: OpportunityStatus.Ignored, retailer: "Filtered");
        var html = await Html(await LoggedIn(), "/?Retailer=Filtered");

        Assert.Contains("Visible Item", html);
        Assert.DoesNotContain("Amazon Item", html);
        Assert.DoesNotContain("Loss Item", html);
        Assert.DoesNotContain("Ignored Item", html);
        Assert.Contains("Hidden by filters", html);
        Assert.Contains("1 with Amazon on the listing", html);
    }

    [Fact]
    public async Task Unchecking_hide_amazon_shows_those_rows()
    {
        Add("Amazon Seller Item", amazon: true, retailer: "AmzShop");
        var html = await Html(await LoggedIn(), "/?Retailer=AmzShop&HideAmazon=false");
        Assert.Contains("Amazon Seller Item", html);
        Assert.Contains("flag-amazon", html);
    }

    [Fact]
    public async Task Stale_rows_are_greyed_out_only_when_included()
    {
        Add("Old Price Item", hoursOld: 30, retailer: "OldShop");
        var c = await LoggedIn();
        Assert.DoesNotContain("Old Price Item", await Html(c, "/?Retailer=OldShop"));

        var html = await Html(c, "/?Retailer=OldShop&IncludeStale=true");
        Assert.Contains("Old Price Item", html);
        Assert.Contains("<tr class=\"stale\">", html);
        Assert.Contains("<strong>stale</strong>", html);
    }

    [Fact]
    public async Task Sorting_by_a_column_orders_the_rows_and_marks_the_header()
    {
        Add("Sort Cheap", profit: 1m, retailer: "SortShop"); Add("Sort Dear", profit: 9m, retailer: "SortShop");
        var c = await LoggedIn();
        var asc = await Html(c, "/?Retailer=SortShop&Sort=Profit&Desc=false");
        Assert.True(asc.IndexOf("Sort Cheap", StringComparison.Ordinal) < asc.IndexOf("Sort Dear", StringComparison.Ordinal));
        Assert.Contains("aria-sort=\"ascending\"", asc);

        var desc = await Html(c, "/?Retailer=SortShop&Sort=Profit&Desc=true");
        Assert.True(desc.IndexOf("Sort Dear", StringComparison.Ordinal) < desc.IndexOf("Sort Cheap", StringComparison.Ordinal));
        Assert.Contains("aria-sort=\"descending\"", desc);
    }

    [Fact]
    public async Task Header_links_keep_the_current_filters()
    {
        Add("Link Keeper", retailer: "KeepShop");
        var html = await Html(await LoggedIn(), "/?Retailer=KeepShop&MinRoi=10");
        Assert.Matches(@"href=""\?Retailer=KeepShop&amp;MinRoi=10&amp;Sort=Profit&amp;Desc=true""", html);
    }

    [Fact]
    public async Task An_unknown_sort_value_does_not_break_the_page()
    {
        var res = await (await LoggedIn()).GetAsync("/?Sort=DROP_TABLE&Desc=banana&Page=abc");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Ranking_toggle_switches_between_risk_adjusted_and_raw()
    {
        Add("Risky Whale", retailer: "RankShop", adj: 25m, risk: RiskLevel.VeryHigh);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuyListDbContext>();
            db.Opportunities.Single(o => o.SourceOffer!.RetailerName == "RankShop").EstMonthlyProfit = 500m;
            db.SaveChanges();
        }
        Add("Safe Minnow", retailer: "RankShop", adj: 200m);
        var c = await LoggedIn();
        var adjusted = await Html(c, "/?Retailer=RankShop");
        Assert.True(adjusted.IndexOf("Safe Minnow", StringComparison.Ordinal) < adjusted.IndexOf("Risky Whale", StringComparison.Ordinal));
        var raw = await Html(c, "/?Retailer=RankShop&Ranking=Raw");
        Assert.True(raw.IndexOf("Risky Whale", StringComparison.Ordinal) < raw.IndexOf("Safe Minnow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paging_links_appear_when_there_is_more_than_one_page()
    {
        for (var i = 0; i < 4; i++) Add("Paged " + i, retailer: "PageShop");
        var html = await Html(await LoggedIn(), "/?Retailer=PageShop&PageSize=25");
        Assert.DoesNotContain("page-link", html);          // 4 rows: one page
        for (var i = 4; i < 30; i++) Add("Paged " + i, retailer: "PageShop");
        html = await Html(await LoggedIn(), "/?Retailer=PageShop&PageSize=25");
        Assert.Contains("page-link", html);
        Assert.Contains("Showing 1–25 of 30", html);
    }

    // ---------------- status actions ----------------
    private async Task<HttpResponseMessage> SetStatus(HttpClient c, int id, string status, string returnUrl)
    {
        var page = await Html(c, "/");
        return await c.PostAsync("/?handler=Status", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Antiforgery(page), ["id"] = id.ToString(), ["status"] = status, ["returnUrl"] = returnUrl }));
    }

    [Fact]
    public async Task Marking_a_row_ignored_hides_it_and_bought_moves_it_to_the_bought_view()
    {
        var ignored = Add("To Ignore", retailer: "ActShop");
        var bought = Add("To Buy", retailer: "ActShop");
        var c = await LoggedIn();

        var res = await SetStatus(c, ignored, "Ignored", "/?Retailer=ActShop");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        await SetStatus(c, bought, "Bought", "/?Retailer=ActShop");

        var list = await Html(c, "/?Retailer=ActShop");
        Assert.DoesNotContain("To Ignore", list);
        Assert.DoesNotContain("To Buy", list);
        var boughtView = await Html(c, "/?View=Bought");
        Assert.Contains("To Buy", boughtView);
        Assert.DoesNotContain("To Ignore", boughtView);
    }

    [Fact]
    public async Task Status_change_redirects_back_to_the_same_filtered_view()
    {
        var id = Add("Redirect Me", retailer: "BackShop");
        var res = await SetStatus(await LoggedIn(), id, "Checked", "/?Retailer=BackShop&MinRoi=5");
        Assert.Equal("/?Retailer=BackShop&MinRoi=5", res.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("javascript:alert(1)")]
    public async Task Status_change_never_redirects_off_site(string returnUrl)
    {
        var id = Add("Safe Redirect", retailer: "SafeShop");
        var res = await SetStatus(await LoggedIn(), id, "Checked", returnUrl);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.DoesNotContain("evil", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_invalid_status_value_is_rejected()
    {
        var id = Add("Bad Status", retailer: "BadShop");
        var res = await SetStatus(await LoggedIn(), id, "99", "/");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Status_posts_need_an_antiforgery_token()
    {
        var id = Add("No Token", retailer: "TokenShop");
        var res = await (await LoggedIn()).PostAsync("/?handler=Status", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["id"] = id.ToString(), ["status"] = "Ignored" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---------------- export ----------------
    [Fact]
    public async Task Export_downloads_the_filtered_list_as_csv_with_a_bom_and_safe_cells()
    {
        Add("=EVIL() Export Item", retailer: "ExpShop"); Add("Hidden Export Item", amazon: true, retailer: "ExpShop");
        var c = await LoggedIn();
        var res = await c.GetAsync("/?Retailer=ExpShop&handler=Export");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("buy-list-", res.Content.Headers.ContentDisposition!.FileName);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"'=EVIL() Export Item\"", csv);       // formula neutralised
        Assert.DoesNotContain("Hidden Export Item", csv);       // same filters as the page
        Assert.Contains("https://expshop.example/p/", csv);     // source link included
    }

    [Fact]
    public async Task Export_link_on_the_page_carries_the_current_filters()
    {
        Add("Export Link", retailer: "LinkShop");
        var html = await Html(await LoggedIn(), "/?Retailer=LinkShop&MinRoi=5");
        Assert.Contains("href=\"/?Retailer=LinkShop&amp;MinRoi=5&amp;handler=Export\"", html);
    }

    // ---------------- empty states ----------------
    [Fact]
    public async Task Bought_view_has_a_helpful_empty_message_when_nothing_is_bought()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuyListDbContext>();
        db.Opportunities.Where(o => o.Status == OpportunityStatus.Bought).ToList().ForEach(o => o.Status = OpportunityStatus.New);
        db.SaveChanges();
        var html = await Html(await LoggedIn(), "/?View=Bought");
        Assert.Contains("Nothing marked as bought yet", html);
    }

    [Fact]
    public async Task Refresh_runs_the_pipeline_and_reports_what_happened()
    {
        // Demo mode (Keepa:UseFake defaults to true): the fake catalogue knows the sample EANs, so this resolves and spends no tokens.
        int offerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuyListDbContext>();
            var first = System.IO.File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "samples", "offers.sample.csv")).Skip(1).First().Split(',');
            var o = new SourceOffer
            {
                RetailerName = "RefreshShop", SourceUrl = "https://refreshshop.example/p/1", BuyPriceExVat = 2.9m, BuyPriceIncVat = 3.48m,
                CheckedAt = DateTime.UtcNow, StockStatus = StockStatus.InStock, RawEan = first[2], RawBrand = first[4], RawMpn = first[3], RawTitle = first[5]
            };
            db.SourceOffers.Add(o);
            db.SaveChanges();
            offerId = o.Id;
        }
        var c = await LoggedIn();
        var page = await Html(c, "/");
        var res = await c.PostAsync("/?handler=Refresh", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Antiforgery(page), ["returnUrl"] = "/" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var after = await Html(c, "/");
        Assert.Contains("Refresh finished", after);
        Assert.Contains("Demo data (no tokens used)", after);
        using var scope2 = _factory.Services.CreateScope();
        var read = scope2.ServiceProvider.GetRequiredService<BuyListDbContext>();
        Assert.True(await read.Opportunities.AnyAsync(o => o.SourceOfferId == offerId));
    }

    [Fact]
    public async Task Refresh_posts_need_an_antiforgery_token()
    {
        var res = await (await LoggedIn()).PostAsync("/?handler=Refresh", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}

public sealed class BuyListEmptyStateTests : IClassFixture<BuyListEmptyStateTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"buylist-empty-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:BuyList", $"Data Source={_db}");
            builder.UseSetting("Seed:OnStartup", "false");
        }
    }

    private readonly Factory _factory;
    public BuyListEmptyStateTests(Factory f) => _factory = f;

    private async Task<HttpClient> LoggedIn()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await (await c.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = token, ["Username"] = "admin", ["Password"] = "changeme" }));
        return c;
    }

    [Fact]
    public async Task Empty_database_says_why_and_points_to_import_then_explains_the_pipeline_once_offers_exist()
    {
        var c = await LoggedIn();
        var html = await (await c.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("No offers yet", html);
        Assert.Contains("Import prices", html);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuyListDbContext>();
            var stub = new Product { Ean = "5012345678900", Title = "Waiting" };
            var ambiguous = new Product { Ean = "96385074", AsinLookupStatus = AsinLookupStatus.Ambiguous };
            db.AddRange(stub, ambiguous,
                new SourceOffer { Product = stub, RetailerName = "S", SourceUrl = "https://s.example/1", CheckedAt = DateTime.UtcNow },
                new SourceOffer { Product = ambiguous, RetailerName = "S", SourceUrl = "https://s.example/2", CheckedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        html = await (await c.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("2 offers imported, but no opportunities yet", html);
        Assert.Contains("still need an Amazon lookup", html);
        Assert.Contains("map to multiple ASINs", html);
        Assert.Contains("Demo mode", html);
        Assert.Contains("Refresh now", html);
    }
}
