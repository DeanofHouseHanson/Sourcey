using BuyList.Application.Formatting;
using BuyList.Application.Keepa;
using BuyList.Application.Options;
using BuyList.Infrastructure.Keepa;
using BuyList.Infrastructure.Seeding;
using BuyList.Application.Enrichment;
using Microsoft.Extensions.Options;

namespace BuyList.Web.Cli;

/// <summary>Maintenance commands: `dotnet run --project src/BuyList.Web -- seed | refresh | check-keepa`.</summary>
public static class CliCommands
{
    public static readonly string[] Names = { "seed", "refresh", "check-keepa" };

    public static async Task<int> RunAsync(string[] args, IServiceProvider root)
    {
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;
        try
        {
            return args[0] switch
            {
                "seed" => await Seed(sp),
                "refresh" => await Refresh(sp),
                "check-keepa" => await CheckKeepa(sp, args.Skip(1).ToArray()),
                _ => 2
            };
        }
        catch (KeepaException ex)
        {
            Console.Error.WriteLine($"Keepa error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> Seed(IServiceProvider sp)
    {
        var r = await sp.GetRequiredService<DemoSeeder>().SeedAsync();
        Console.WriteLine(r.Message);
        return r.Seeded || r.Message.Contains("already") ? 0 : 1;
    }

    private static async Task<int> Refresh(IServiceProvider sp)
    {
        var keepa = sp.GetRequiredService<IOptions<KeepaOptions>>().Value;
        Console.WriteLine(keepa.UseFake
            ? "Using the FAKE Keepa client (Keepa:UseFake=true); no tokens are spent."
            : $"Using live Keepa; this run will spend at most {keepa.MaxTokensPerRun} tokens and keep {keepa.MinTokensReserve} in reserve.");

        var r = await sp.GetRequiredService<IRefreshPipeline>().RunAsync();
        var e = r.Enrichment;
        Console.WriteLine($"Matching:     {r.Matching.TotalMatched} matched ({r.Matching.ProductsCreated} new products), {r.Matching.Unmatchable} unmatchable");
        Console.WriteLine($"Keepa:        {e.CodesLookedUp} codes looked up ({e.AsinsResolved} resolved, {e.CodesNotFound} not found, {e.CodesAmbiguous} ambiguous), " +
                          $"{e.ProductsRefreshed} refreshed");
        Console.WriteLine($"Tokens:       {e.TokensUsed} used, {(e.TokensLeft?.ToString() ?? "?")} left" +
                          (e.ProductsDeferred > 0 ? $", {e.ProductsDeferred} deferred to the next run (budget)" : "") +
                          (e.Throttled ? ", THROTTLED by Keepa" : ""));
        Console.WriteLine($"Opportunities: {r.Opportunities.Created} created, {r.Opportunities.Updated} updated, {r.Opportunities.Removed} removed " +
                          $"({r.Opportunities.SkippedNoPrice} without a Buy Box price)");
        return 0;
    }

    /// <summary>Fetches a few ASINs from the REAL API and prints what we parsed. Capped at 3 ASINs; `--tokens` only shows the (free) balance.</summary>
    private static async Task<int> CheckKeepa(IServiceProvider sp, string[] args)
    {
        var o = sp.GetRequiredService<IOptions<KeepaOptions>>().Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey))
        {
            Console.Error.WriteLine("No Keepa key: set Keepa__ApiKey (env) or Keepa:ApiKey (user secrets).");
            return 1;
        }
        var client = sp.GetRequiredService<KeepaClient>();

        var before = await client.GetTokenStatusAsync();
        Console.WriteLine($"Tokens: {before.TokensLeft} left, refilling {before.RefillRatePerMinute}/min (checking costs nothing).");
        var asins = args.Where(a => !a.StartsWith("--")).Select(a => a.ToUpperInvariant()).ToList();
        if (args.Contains("--tokens") || asins.Count == 0)
        {
            if (asins.Count == 0) Console.WriteLine("Pass up to 3 ASINs to fetch, e.g.: check-keepa B0XXXXXXXX");
            return 0;
        }
        if (asins.Count > 3) { Console.Error.WriteLine("Refusing more than 3 ASINs in a check; use `refresh` for bulk."); return 1; }

        var res = await client.GetByAsinsAsync(asins, asins.Count * Math.Max(o.TokensPerProduct, 3));
        Console.WriteLine($"Spent {res.TokensUsed} token(s); {res.Tokens.TokensLeft} left.");
        if (res.NotFetched.Count > 0) Console.WriteLine($"Not fetched (token budget): {string.Join(", ", res.NotFetched)}");
        if (res.Throttled) Console.WriteLine("Keepa reported not enough tokens.");

        foreach (var p in res.Products)
        {
            Console.WriteLine();
            Console.WriteLine($"{p.Asin}  {p.Title}");
            Console.WriteLine($"  brand={p.Brand}  mpn={p.Mpn}  eans={string.Join(",", p.Eans)}");
            Console.WriteLine($"  90d avg Buy Box: {Gbp.Format(p.Avg90BuyBox)}   sales rank: {p.SalesRankCurrent?.ToString() ?? Gbp.NoData} (90d avg {p.SalesRankAvg90?.ToString() ?? Gbp.NoData})");
            Console.WriteLine($"  monthly sold: {p.MonthlySold?.ToString() ?? Gbp.NoData}   FBA sellers: {p.FbaSellerCount?.ToString() ?? Gbp.NoData}{(p.FbaSellerCountIsApprox ? " (approx: all new offers)" : "")}   Amazon on listing: {p.AmazonOnListing}");
            Console.WriteLine($"  weight: {(p.WeightGrams is null ? Gbp.NoData : p.WeightGrams + " g")}   FBA fee (Keepa): {Gbp.Format(p.FbaPickAndPackFee)}");
        }
        var missing = asins.Except(res.Products.Select(p => p.Asin)).Except(res.NotFetched).ToList();
        if (missing.Count > 0) Console.WriteLine($"\nKeepa returned nothing for: {string.Join(", ", missing)}");
        return 0;
    }
}
