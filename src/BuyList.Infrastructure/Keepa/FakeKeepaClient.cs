using System.Reflection;
using System.Text.Json;
using BuyList.Application.Import;
using BuyList.Application.Keepa;

namespace BuyList.Infrastructure.Keepa;

/// <summary>
/// Offline stand-in for Keepa: a fixed, deterministic catalogue keyed on the EANs in samples/offers.sample.csv, with
/// a spread of good and bad opportunities, Amazon-on-listing items and missing data. Spends no tokens.
/// Its ASINs start with <see cref="AsinPrefix"/> so the real client path can recognise and skip them.
/// </summary>
public class FakeKeepaClient : IKeepaClient
{
    public const string AsinPrefix = "B0FAKE";

    private sealed record Entry(
        string Asin, string? Title, string? Brand, string? ImageUrl, string? Mpn, List<string> Eans, decimal? Avg90BuyBox,
        int? SalesRankCurrent, int? SalesRankAvg90, int? SalesRankDrops30, int? FbaSellerCount, bool FbaSellerCountIsApprox,
        bool AmazonOnListing, int? MonthlySold, int? WeightGrams, decimal? FbaPickAndPackFee);

    private static readonly Lazy<IReadOnlyList<Entry>> Catalogue = new(Load);
    private readonly TimeProvider _time;

    public FakeKeepaClient(TimeProvider time) => _time = time;

    public static bool IsFakeAsin(string? asin) => asin?.StartsWith(AsinPrefix, StringComparison.Ordinal) == true;

    public Task<KeepaTokenStatus> GetTokenStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new KeepaTokenStatus(9999, 60, 60_000));

    public Task<KeepaFetchResult> GetByAsinsAsync(IReadOnlyList<string> asins, int maxTokens, CancellationToken ct = default)
    {
        var wanted = asins.ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(Result(Catalogue.Value.Where(e => wanted.Contains(e.Asin))));
    }

    public Task<KeepaFetchResult> GetByCodesAsync(IReadOnlyList<string> codes, int maxTokens, CancellationToken ct = default)
    {
        var wanted = codes.Select(c => Gtin.NormaliseOrNull(c) ?? c).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(Result(Catalogue.Value.Where(e => e.Eans.Any(wanted.Contains))));
    }

    private KeepaFetchResult Result(IEnumerable<Entry> entries)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var products = entries.Select(e => new KeepaProduct(e.Asin, e.Title, e.Brand, e.ImageUrl, e.Mpn, e.Eans, e.Avg90BuyBox,
            e.SalesRankCurrent, e.SalesRankAvg90, e.SalesRankDrops30, e.FbaSellerCount, e.FbaSellerCountIsApprox,
            e.AmazonOnListing, e.MonthlySold, e.WeightGrams, e.FbaPickAndPackFee, now)).ToList();
        return new KeepaFetchResult(products, Array.Empty<string>(), 0, new KeepaTokenStatus(9999, 60, 60_000), false);
    }

    private static IReadOnlyList<Entry> Load()
    {
        var asm = typeof(FakeKeepaClient).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("fake-catalogue.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<List<Entry>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
