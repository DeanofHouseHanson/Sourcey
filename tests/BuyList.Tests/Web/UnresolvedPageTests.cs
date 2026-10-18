using System.Net;
using System.Text.RegularExpressions;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BuyList.Tests.Web;

public sealed class UnresolvedPageTests : IClassFixture<UnresolvedPageTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"buylist-unresolved-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:BuyList", $"Data Source={_db}");
            builder.UseSetting("Seed:OnStartup", "false");
        }
    }

    private readonly Factory _factory;
    public UnresolvedPageTests(Factory f) => _factory = f;

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

    private int SeedAmbiguous(string ean, int options, string title = "Supplier Widget")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuyListDbContext>();
        var p = new Product { Ean = ean, Title = title, AsinLookupStatus = AsinLookupStatus.Ambiguous };
        for (var i = 1; i <= options; i++)
            p.AsinCandidates.Add(new AsinCandidate
            {
                Asin = $"B0{ean[^6..]}{i:D2}", Title = $"Listing number {i}", Brand = "Acme",
                SalesRank = i == 2 ? null : 1000 * i,               // option 2 has no rank
                MonthlySales = i == 3 ? null : 40 * i,              // option 3 has no sales figure
                Avg90BuyBox = i == 1 ? null : 10m + i,              // option 1 has no price
                AmazonOnListing = i == 1, CapturedAt = DateTime.UtcNow
            });
        p.SourceOffers.Add(new SourceOffer
        {
            RetailerName = "Shop", SourceUrl = $"https://shop.example/{ean}", BuyPriceExVat = 5, BuyPriceIncVat = 6,
            CheckedAt = DateTime.UtcNow, StockStatus = StockStatus.InStock
        });
        db.Products.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    [Fact]
    public async Task Requires_login()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/Unresolved")).StatusCode);
    }

    [Fact]
    public async Task Shows_the_multiple_asins_message_and_a_condensed_list_of_options()
    {
        SeedAmbiguous("5000000000011", 3);
        var html = await (await (await LoggedIn()).GetAsync("/Unresolved")).Content.ReadAsStringAsync();

        Assert.Contains("Maps to multiple ASINs", html);
        Assert.Contains("5000000000011", html);
        Assert.Contains("Listing number 1", html);
        Assert.Contains("Listing number 3", html);
        Assert.Contains("https://www.amazon.co.uk/dp/B0000011", html);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", html);
        Assert.Contains("Use this one", html);
        Assert.DoesNotContain("<details", html.Substring(html.IndexOf("5000000000011", StringComparison.Ordinal), 200));
    }

    [Fact]
    public async Task Missing_values_in_options_just_say_no_data()
    {
        SeedAmbiguous("5000000000028", 3);
        var html = await (await (await LoggedIn()).GetAsync("/Unresolved")).Content.ReadAsStringAsync();
        var section = html.Substring(html.IndexOf("5000000000028", StringComparison.Ordinal));
        section = section.Substring(0, section.IndexOf("</section>", StringComparison.Ordinal));

        Assert.Equal(3, Regex.Matches(section, "no data").Count);   // option 1 price, option 2 rank, option 3 sales
        Assert.DoesNotContain("null", section, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("N/A", section);
    }

    [Fact]
    public async Task More_than_five_options_are_collapsed_behind_a_count()
    {
        SeedAmbiguous("5000000000035", 8);
        var html = await (await (await LoggedIn()).GetAsync("/Unresolved")).Content.ReadAsStringAsync();
        Assert.Contains("+3 more options", html);
        Assert.Contains("<details", html);
    }

    [Fact]
    public async Task Choosing_an_option_through_the_page_settles_the_product()
    {
        var id = SeedAmbiguous("5000000000042", 2);
        var c = await LoggedIn();
        var page = await (await c.GetAsync("/Unresolved")).Content.ReadAsStringAsync();

        var res = await c.PostAsync("/Unresolved?handler=Choose", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Antiforgery(page), ["productId"] = id.ToString(), ["asin"] = "B0000042" + "02" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var after = await (await c.GetAsync("/Unresolved")).Content.ReadAsStringAsync();
        Assert.Contains("Using B000004202", after);
        using var scope = _factory.Services.CreateScope();
        var p = scope.ServiceProvider.GetRequiredService<BuyListDbContext>().Products.Single(x => x.Id == id);
        Assert.Equal("B000004202", p.Asin);
    }

    [Fact]
    public async Task A_made_up_asin_is_refused_with_a_message()
    {
        var id = SeedAmbiguous("5000000000059", 2);
        var c = await LoggedIn();
        var page = await (await c.GetAsync("/Unresolved")).Content.ReadAsStringAsync();
        await c.PostAsync("/Unresolved?handler=Choose", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Antiforgery(page), ["productId"] = id.ToString(), ["asin"] = "B0EVILEVIL" }));

        var after = await (await c.GetAsync("/Unresolved")).Content.ReadAsStringAsync();
        Assert.Contains("isn&#x27;t one of the options", after);
        using var scope = _factory.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetRequiredService<BuyListDbContext>().Products.Single(x => x.Id == id).Asin);
    }

    [Fact]
    public async Task Posts_without_an_antiforgery_token_are_rejected()
    {
        var id = SeedAmbiguous("5000000000066", 2);
        var res = await (await LoggedIn()).PostAsync("/Unresolved?handler=Choose", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["productId"] = id.ToString(), ["asin"] = "B000000602" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
