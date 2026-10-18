using BuyList.Application.Enrichment;
using BuyList.Application.Keepa;
using BuyList.Application.Options;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Keepa;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuyList.Infrastructure.Enrichment;

/// <summary>
/// Spends Keepa tokens in priority order, inside a per-run cap:
///  1. resolve ASINs for stub products (EAN lookup) - the lookup response already carries the market data, so it
///     is saved as a snapshot immediately and costs no second request;
///  2. refresh products whose data is older than Keepa:RefreshAgeHours (never-fetched first, then oldest).
/// Only products with at least one buyable (not out-of-stock) offer are worth tokens. Anything the budget can't
/// cover is deferred, not dropped.
/// </summary>
public class EnrichmentService : IEnrichmentService
{
    private readonly BuyListDbContext _db;
    private readonly IKeepaClient _keepa;
    private readonly KeepaOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger<EnrichmentService> _log;

    public EnrichmentService(BuyListDbContext db, IKeepaClient keepa, IOptions<KeepaOptions> options, TimeProvider time, ILogger<EnrichmentService> log)
    {
        _db = db;
        _keepa = keepa;
        _o = options.Value;
        _time = time;
        _log = log;
    }

    public async Task<EnrichmentSummary> EnrichAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var budget = _o.MaxTokensPerRun;
        int used = 0, looked = 0, resolved = 0, notFound = 0, ambiguous = 0, refreshed = 0, snapshots = 0, deferred = 0;
        var throttled = false;

        var status = await _keepa.GetTokenStatusAsync(ct);   // free; fail early on a bad key
        var tokensLeft = status.TokensLeft;

        // ---- 1. resolve stub products by EAN ----
        var retryBefore = now.AddDays(-_o.UnresolvedRetryDays);
        var stubs = await _db.Products
            .Where(p => p.Asin == null && p.Ean != null
                        && (p.KeepaCheckedAt == null || p.KeepaCheckedAt < retryBefore)
                        && p.SourceOffers.Any(o => o.StockStatus != StockStatus.OutOfStock))
            .OrderBy(p => p.Id).ToListAsync(ct);

        if (stubs.Count > 0)
        {
            var res = await _keepa.GetByCodesAsync(stubs.Select(s => s.Ean!).Distinct().ToList(), budget - used, ct);
            used += res.TokensUsed; tokensLeft = res.Tokens.TokensLeft ?? tokensLeft; throttled |= res.Throttled;
            var notSent = res.NotFetched.ToHashSet(StringComparer.Ordinal);

            foreach (var stub in stubs)
            {
                if (notSent.Contains(stub.Ean!)) { deferred++; continue; }
                looked++;
                var matches = res.Products.Where(k => k.Eans.Contains(stub.Ean!)).DistinctBy(k => k.Asin).ToList();
                stub.KeepaCheckedAt = now;

                if (matches.Count == 0) { stub.AsinLookupStatus = AsinLookupStatus.NotFound; notFound++; }
                else if (matches.Count > 1) { stub.AsinLookupStatus = AsinLookupStatus.Ambiguous; ambiguous++; }
                else
                {
                    var target = await ResolveAsync(stub, matches[0], ct);
                    snapshots += AddSnapshot(target, matches[0]);
                    resolved++;
                }
            }
            await _db.SaveChangesAsync(ct);
        }

        // ---- 2. refresh stale market data ----
        if (!throttled && budget - used > 0)
        {
            var staleBefore = now.AddHours(-_o.RefreshAgeHours);
            var due = await _db.Products
                .Where(p => p.Asin != null && !p.Asin.StartsWith(FakeKeepaClient.AsinPrefix)
                            && (p.KeepaCheckedAt == null || p.KeepaCheckedAt < staleBefore)
                            && p.SourceOffers.Any(o => o.StockStatus != StockStatus.OutOfStock))
                .OrderBy(p => p.KeepaCheckedAt == null ? 0 : 1).ThenBy(p => p.KeepaCheckedAt).ThenBy(p => p.Id)
                .ToListAsync(ct);

            if (due.Count > 0)
            {
                var res = await _keepa.GetByAsinsAsync(due.Select(p => p.Asin!).ToList(), budget - used, ct);
                used += res.TokensUsed; tokensLeft = res.Tokens.TokensLeft ?? tokensLeft; throttled |= res.Throttled;
                var notSent = res.NotFetched.ToHashSet(StringComparer.Ordinal);
                var byAsin = res.Products.ToDictionary(k => k.Asin, StringComparer.Ordinal);

                foreach (var product in due)
                {
                    if (notSent.Contains(product.Asin!)) { deferred++; continue; }
                    product.KeepaCheckedAt = now;   // asked, so don't ask again until it's stale (even if Keepa had nothing)
                    if (!byAsin.TryGetValue(product.Asin!, out var k)) continue;
                    ApplyProductData(product, k);
                    snapshots += AddSnapshot(product, k);
                    refreshed++;
                }
                await _db.SaveChangesAsync(ct);
            }
        }

        _log.LogInformation("Keepa enrichment: {Resolved} resolved, {Refreshed} refreshed, {Used} token(s) used, {Left} left, {Deferred} deferred",
            resolved, refreshed, used, tokensLeft, deferred);
        return new EnrichmentSummary(looked, resolved, notFound, ambiguous, refreshed, snapshots, used, tokensLeft, deferred, throttled);
    }

    /// <summary>
    /// Attach the ASIN to the stub. If a product with that ASIN already exists (e.g. it was matched earlier by brand + MPN),
    /// fold the stub into it instead, since ASIN is unique.
    /// </summary>
    private async Task<Product> ResolveAsync(Product stub, KeepaProduct k, CancellationToken ct)
    {
        var existing = await _db.Products.FirstOrDefaultAsync(p => p.Asin == k.Asin && p.Id != stub.Id, ct);
        if (existing is null)
        {
            stub.Asin = k.Asin;
            stub.AsinLookupStatus = AsinLookupStatus.Resolved;
            ApplyProductData(stub, k);
            return stub;
        }

        foreach (var offer in await _db.SourceOffers.Where(o => o.ProductId == stub.Id).ToListAsync(ct))
            offer.ProductId = existing.Id;
        existing.Ean ??= stub.Ean;
        existing.KeepaCheckedAt = stub.KeepaCheckedAt;
        _db.Products.Remove(stub);
        ApplyProductData(existing, k);
        return existing;
    }

    private static void ApplyProductData(Product p, KeepaProduct k)
    {
        p.Title = k.Title ?? p.Title;          // Amazon's title is the authoritative one
        p.Brand ??= k.Brand;
        p.Mpn ??= k.Mpn;
        p.ImageUrl = k.ImageUrl ?? p.ImageUrl;
        if (p.Ean is null && k.Eans.Count == 1) p.Ean = k.Eans[0];
    }

    private int AddSnapshot(Product p, KeepaProduct k)
    {
        _db.AmazonSnapshots.Add(new AmazonSnapshot
        {
            Product = p,
            CapturedAt = k.FetchedAt,
            Avg90BuyBox = k.Avg90BuyBox,
            SalesRank = k.SalesRankCurrent,
            MonthlySales = k.MonthlySold,
            FbaSellerCount = k.FbaSellerCount ?? 0,
            FbaSellerCountIsApprox = k.FbaSellerCountIsApprox || k.FbaSellerCount is null,
            AmazonOnListing = k.AmazonOnListing,
            WeightGrams = k.WeightGrams,
            KeepaFbaFee = k.FbaPickAndPackFee
        });
        return 1;
    }
}
