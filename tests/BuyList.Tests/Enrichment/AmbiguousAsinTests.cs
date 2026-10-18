using BuyList.Application.Eligibility;
using BuyList.Application.Enrichment;
using BuyList.Application.Formatting;
using BuyList.Application.Options;
using BuyList.Application.Profit;
using BuyList.Application.Ranking;
using BuyList.Application.Risk;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Enrichment;
using BuyList.Infrastructure.Opportunities;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static BuyList.Tests.Enrichment.ScriptedKeepa;

namespace BuyList.Tests.Enrichment;

public sealed class AmbiguousAsinTests : IDisposable
{
    private const string Ean = "5012345678900";
    private readonly TestDb _testDb = new();
    private readonly FixedTime _time = new();
    private readonly ScriptedKeepa _keepa = new();
    public void Dispose() => _testDb.Dispose();

    private Task Enrich()
    {
        var db = _testDb.NewContext();
        return new EnrichmentService(db, _keepa, Options.Create(new KeepaOptions { MaxTokensPerRun = 300, UnresolvedRetryDays = 7 }), _time,
            NullLogger<EnrichmentService>.Instance).EnrichAsync();
    }

    private AsinResolutionService Resolver(BuyListDbContext db)
    {
        var profit = Options.Create(new ProfitOptions());
        var fees = new ApproximateFeeEstimator(profit, Options.Create(new FbaFeeOptions { Tiers = { new FbaFeeTier { Name = "500g", MaxWeightGrams = 500, Fee = 2.90m } } }));
        var opps = new OpportunityService(db, new ProfitCalculator(profit, fees),
            new OpportunityScorer(Options.Create(new RankingOptions()), new RiskClassifier(Options.Create(new RiskOptions()))),
            new ManualEligibilityChecker(), _time);
        return new AsinResolutionService(db, opps);
    }

    private static global::BuyList.Application.Keepa.KeepaProduct Option(string asin, int? rank, decimal? price = 24.00m, int? sold = 60) =>
        Kp(asin, Ean, price: price, sold: sold) with { SalesRankCurrent = rank, Title = "Listing " + asin };

    private async Task<int> SeedAmbiguous(params global::BuyList.Application.Keepa.KeepaProduct[] options)
    {
        using (var db = _testDb.NewContext()) Seed.Stub(db, Ean);
        _keepa.Catalogue.AddRange(options);
        await Enrich();
        using var read = _testDb.NewContext();
        return read.Products.Single(p => p.Ean == Ean).Id;
    }

    // ---- storing the options ----
    [Fact]
    public async Task Ambiguous_lookups_keep_every_option_best_rank_first_unranked_last()
    {
        await SeedAmbiguous(Option("B00UNRANKED", null), Option("B00MIDRANK0", 5000), Option("B00BESTRANK", 120));

        using var db = _testDb.NewContext();
        var p = db.Products.Include(x => x.AsinCandidates).Single();
        Assert.Equal(AsinLookupStatus.Ambiguous, p.AsinLookupStatus);
        Assert.Null(p.Asin);
        Assert.Equal(new[] { "B00BESTRANK", "B00MIDRANK0", "B00UNRANKED" },
            p.AsinCandidates.OrderBy(c => c.SalesRank ?? int.MaxValue).Select(c => c.Asin));
        var best = p.AsinCandidates.Single(c => c.Asin == "B00BESTRANK");
        Assert.Equal("Listing B00BESTRANK", best.Title);
        Assert.Equal(120, best.SalesRank);
        Assert.Equal(60, best.MonthlySales);
        Assert.Equal(24.00m, best.Avg90BuyBox);
        Assert.Empty(db.AmazonSnapshots);                // no data is attributed to the product until one is chosen
    }

    [Fact]
    public async Task At_most_ten_options_are_kept()
    {
        await SeedAmbiguous(Enumerable.Range(1, 14).Select(i => Option($"B00OPTION{i:D2}", 100 * i)).ToArray());
        using var db = _testDb.NewContext();
        Assert.Equal(10, db.AsinCandidates.Count());
        Assert.DoesNotContain(db.AsinCandidates, c => c.SalesRank > 1000);   // the worst-ranked were dropped
    }

    [Fact]
    public async Task A_later_lookup_that_settles_on_one_asin_clears_the_old_options()
    {
        await SeedAmbiguous(Option("B00AAAAAAA", 100), Option("B00BBBBBBB", 200));
        _keepa.Catalogue.RemoveAll(p => p.Asin == "B00BBBBBBB");     // Keepa now knows only one listing
        _time.Now = _time.Now.AddDays(8);                            // past the retry window

        await Enrich();

        using var db = _testDb.NewContext();
        Assert.Empty(db.AsinCandidates);
        var p = db.Products.Single();
        Assert.Equal("B00AAAAAAA", p.Asin);
        Assert.Equal(AsinLookupStatus.Resolved, p.AsinLookupStatus);
    }

    // ---- choosing ----
    [Fact]
    public async Task Choosing_an_option_assigns_it_saves_its_data_and_creates_the_opportunity_without_tokens()
    {
        var id = await SeedAmbiguous(Option("B00AAAAAAA", 100), Option("B00BBBBBBB", 200, price: 30.00m));
        var callsBefore = _keepa.Calls.Count;

        ChooseAsinOutcome outcome;
        using (var db = _testDb.NewContext()) outcome = await Resolver(db).ChooseAsync(id, "B00BBBBBBB");

        Assert.Equal(ChooseAsinOutcome.Chosen, outcome);
        Assert.Equal(callsBefore, _keepa.Calls.Count);               // nothing more asked of Keepa
        using var read = _testDb.NewContext();
        var p = read.Products.Single();
        Assert.Equal("B00BBBBBBB", p.Asin);
        Assert.Equal(AsinLookupStatus.Resolved, p.AsinLookupStatus);
        Assert.Empty(read.AsinCandidates);
        Assert.Equal(30.00m, read.AmazonSnapshots.Single().Avg90BuyBox);   // the chosen option's data, not the other's
        var opp = read.Opportunities.Single();
        Assert.True(opp.NetProfit > 0);                                   // appears on the Buy List straight away
        Assert.Equal(p.Id, opp.ProductId);
    }

    [Fact]
    public async Task An_asin_that_was_not_offered_cannot_be_chosen()
    {
        var id = await SeedAmbiguous(Option("B00AAAAAAA", 100), Option("B00BBBBBBB", 200));
        using var db = _testDb.NewContext();
        Assert.Equal(ChooseAsinOutcome.NotACandidate, await Resolver(db).ChooseAsync(id, "B00ZZZZZZZ"));
        using var read = _testDb.NewContext();
        Assert.Null(read.Products.Single().Asin);
        Assert.Equal(2, read.AsinCandidates.Count());
    }

    [Fact]
    public async Task Choosing_for_a_product_that_is_not_ambiguous_is_refused()
    {
        int id;
        using (var db = _testDb.NewContext()) id = Seed.Stub(db, "96385074", asin: "B00RESOLVED").Id;
        using var db2 = _testDb.NewContext();
        Assert.Equal(ChooseAsinOutcome.NotACandidate, await Resolver(db2).ChooseAsync(id, "B00RESOLVED"));
    }

    [Fact]
    public async Task Choosing_for_a_missing_product_is_reported()
    {
        using var db = _testDb.NewContext();
        Assert.Equal(ChooseAsinOutcome.ProductNotFound, await Resolver(db).ChooseAsync(999, "B00AAAAAAA"));
    }

    [Fact]
    public async Task Choosing_an_asin_we_already_hold_merges_the_offers_into_that_product()
    {
        int existingId;
        using (var db = _testDb.NewContext()) existingId = Seed.Stub(db, "96385074", asin: "B00AAAAAAA", checkedAt: _time.Now.UtcDateTime).Id;
        var id = await SeedAmbiguous(Option("B00AAAAAAA", 100), Option("B00BBBBBBB", 200));

        using (var db = _testDb.NewContext()) await Resolver(db).ChooseAsync(id, "B00AAAAAAA");

        using var read = _testDb.NewContext();
        Assert.Equal(existingId, read.Products.Single().Id);
        Assert.Equal(2, read.SourceOffers.Count(o => o.ProductId == existingId));
        Assert.Empty(read.AsinCandidates);
    }

    // ---- dismissing ----
    [Fact]
    public async Task None_of_these_marks_the_product_not_found_and_drops_the_options()
    {
        var id = await SeedAmbiguous(Option("B00AAAAAAA", 100), Option("B00BBBBBBB", 200));
        using (var db = _testDb.NewContext()) Assert.True(await Resolver(db).DismissAsync(id));

        using var read = _testDb.NewContext();
        Assert.Equal(AsinLookupStatus.NotFound, read.Products.Single().AsinLookupStatus);
        Assert.Empty(read.AsinCandidates);
        Assert.Null(read.Products.Single().Asin);
    }

    [Fact]
    public async Task Dismissing_a_product_that_is_not_ambiguous_does_nothing()
    {
        int id;
        using (var db = _testDb.NewContext()) id = Seed.Stub(db, "96385074", asin: "B00RESOLVED").Id;
        using var db2 = _testDb.NewContext();
        Assert.False(await Resolver(db2).DismissAsync(id));
    }

    // ---- "no data" wording ----
    [Fact]
    public void Missing_values_are_shown_as_plain_no_data()
    {
        Assert.Equal("no data", Gbp.Format((decimal?)null));
        Assert.Equal("£12.50", Gbp.Format((decimal?)12.5m));
        Assert.Equal("no data", Gbp.NoData);
    }
}
