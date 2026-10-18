using BuyList.Application.Import;
using BuyList.Application.Options;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Import;
using BuyList.Infrastructure.Keepa;
using BuyList.Infrastructure.Opportunities;
using BuyList.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Enrichment;

public sealed class FakeKeepaAndSeedTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly FixedTime _time = new();
    public void Dispose() => _testDb.Dispose();

    private static string SamplePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", "offers.sample.csv"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "offers.sample.csv");
    }

    private static IReadOnlyList<string> SampleEans() =>
        File.ReadAllLines(SamplePath()).Skip(1).Select(l => l.Split(',')[2]).ToList();

    [Fact]
    public async Task Every_sample_ean_resolves_in_the_fake_catalogue_to_a_distinct_fake_asin()
    {
        var res = await new FakeKeepaClient(_time).GetByCodesAsync(SampleEans().ToList(), 0);
        Assert.Equal(SampleEans().Count, res.Products.Count);
        Assert.Equal(res.Products.Count, res.Products.Select(p => p.Asin).Distinct().Count());
        Assert.All(res.Products, p => Assert.True(FakeKeepaClient.IsFakeAsin(p.Asin)));
        Assert.Equal(0, res.TokensUsed);
    }

    [Fact]
    public async Task Fake_catalogue_is_deterministic_and_covers_the_awkward_cases()
    {
        var a = (await new FakeKeepaClient(_time).GetByCodesAsync(SampleEans().ToList(), 0)).Products;
        var b = (await new FakeKeepaClient(_time).GetByCodesAsync(SampleEans().ToList(), 0)).Products;
        Assert.Equal(a.Select(p => (p.Asin, p.Avg90BuyBox, p.MonthlySold)), b.Select(p => (p.Asin, p.Avg90BuyBox, p.MonthlySold)));

        Assert.Contains(a, p => p.AmazonOnListing);
        Assert.Contains(a, p => p.MonthlySold is null);        // "no sales data" case
        Assert.Contains(a, p => p.Avg90BuyBox is null);        // no Buy Box price case
        Assert.Contains(a, p => p.FbaSellerCountIsApprox);
        Assert.Contains(a, p => p.MonthlySold < 10);           // very high risk
        Assert.Contains(a, p => p.MonthlySold >= 300);         // very low risk
    }

    [Fact]
    public async Task Fake_can_be_queried_by_asin_and_by_upc_spelling()
    {
        var fake = new FakeKeepaClient(_time);
        var first = (await fake.GetByCodesAsync(new[] { SampleEans()[0] }, 0)).Products.Single();
        Assert.Single((await fake.GetByAsinsAsync(new[] { first.Asin }, 0)).Products);
        Assert.Empty((await fake.GetByCodesAsync(new[] { "5012345678900" }, 0)).Products);   // unknown code
    }

    private DemoSeeder Seeder(BuyList.Infrastructure.Persistence.BuyListDbContext db)
    {
        var profit = Options.Create(new ProfitOptions());
        var fees = new BuyList.Application.Profit.ApproximateFeeEstimator(profit, Options.Create(new FbaFeeOptions { Tiers = { new FbaFeeTier { Name = "500g", MaxWeightGrams = 500, Fee = 2.90m } } }));
        var opps = new OpportunityService(db, new BuyList.Application.Profit.ProfitCalculator(profit, fees),
            new BuyList.Application.Ranking.OpportunityScorer(Options.Create(new RankingOptions()), new BuyList.Application.Risk.RiskClassifier(Options.Create(new RiskOptions()))),
            new BuyList.Application.Eligibility.ManualEligibilityChecker(), _time);
        return new DemoSeeder(db, new OfferFileValidator(new OfferCsvParser(), new OfferRowValidator(profit)),
            new OfferImportService(db, new ProductMatcher(db), _time), opps,
            Options.Create(new KeepaOptions { UseFake = false /* seeding must still stay offline */ }), _time,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task Seeding_an_empty_database_produces_a_varied_populated_buy_list()
    {
        using var db = _testDb.NewContext();
        var r = await Seeder(db).SeedAsync(SamplePath());

        Assert.True(r.Seeded, r.Message);
        Assert.Equal(0, r.Enrichment!.TokensUsed);                   // offline, even though UseFake=false was configured
        using var read = _testDb.NewContext();
        var opps = read.Opportunities.AsNoTracking().ToList();
        Assert.InRange(opps.Count, 25, 38);
        Assert.True(opps.Count(o => o.NetProfit > 0) >= 20);          // plenty to look at...
        Assert.Contains(opps, o => o.NetProfit < 0);                  // ...and some duds the filters must hide
        Assert.True(opps.Select(o => o.RiskLevel).Distinct().Count() >= 4);
        Assert.Contains(opps, o => o.Flags.HasFlag(OpportunityFlags.NoSalesData));
        Assert.Equal(opps.Count, opps.Select(o => o.SourceOfferId).Distinct().Count());
        Assert.All(opps, o => Assert.Equal(OpportunityStatus.New, o.Status));
    }

    [Fact]
    public async Task Seeding_twice_is_harmless()
    {
        using (var db = _testDb.NewContext()) await Seeder(db).SeedAsync(SamplePath());
        using var db2 = _testDb.NewContext();
        var again = await Seeder(db2).SeedAsync(SamplePath());
        Assert.False(again.Seeded);
        using var read = _testDb.NewContext();
        Assert.Equal(38, read.SourceOffers.Count());
    }

    [Fact]
    public async Task Seeding_never_overwrites_an_existing_database()
    {
        using (var db = _testDb.NewContext())
        {
            db.SourceOffers.Add(new BuyList.Domain.Entities.SourceOffer { RetailerName = "Mine", SourceUrl = "https://mine.example/1", CheckedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        using var db2 = _testDb.NewContext();
        Assert.False((await Seeder(db2).SeedAsync(SamplePath())).Seeded);
    }
}
