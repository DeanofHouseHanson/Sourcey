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
    private const int MaxCandidates = 10;

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
                if (matches.Count != 1 && stub.AsinLookupStatus != AsinLookupStatus.Ambiguous) { /* nothing stale to clear */ }
                else if (matches.Count <= 1)   // a retry has settled it: the old option list no longer applies
                    _db.AsinCandidates.RemoveRange(await _db.AsinCandidates.Where(c => c.ProductId == stub.Id).ToListAsync(ct));

                if (matches.Count == 0) { stub.AsinLookupStatus = AsinLookupStatus.NotFound; notFound++; }
                else if (matches.Count > 1)
                {
                    stub.AsinLookupStatus = AsinLookupStatus.Ambiguous;
                    await StoreCandidatesAsync(stub, matches, ct);   // so the owner can choose without spending more tokens
                    ambiguous++;
                }
                else
                {
                    var target = await ProductAssignment.ResolveAsync(_db, stub, matches[0], ct);
                    ProductAssignment.AddSnapshot(_db, target, matches[0]);
                    snapshots++;
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
                    ProductAssignment.ApplyProductData(product, k);
                    ProductAssignment.AddSnapshot(_db, product, k);
                    snapshots++;
                    refreshed++;
                }
                await _db.SaveChangesAsync(ct);
            }
        }

        _log.LogInformation("Keepa enrichment: {Resolved} resolved, {Refreshed} refreshed, {Used} token(s) used, {Left} left, {Deferred} deferred",
            resolved, refreshed, used, tokensLeft, deferred);
        return new EnrichmentSummary(looked, resolved, notFound, ambiguous, refreshed, snapshots, used, tokensLeft, deferred, throttled);
    }

    /// <summary>Keep the best options (best-ranked first, unranked last) in place of any older candidate list.</summary>
    private async Task StoreCandidatesAsync(Domain.Entities.Product stub, List<Application.Keepa.KeepaProduct> matches, CancellationToken ct)
    {
        _db.AsinCandidates.RemoveRange(await _db.AsinCandidates.Where(c => c.ProductId == stub.Id).ToListAsync(ct));
        foreach (var k in matches.OrderBy(k => k.SalesRankCurrent ?? int.MaxValue).ThenBy(k => k.Asin, StringComparer.Ordinal).Take(MaxCandidates))
            _db.AsinCandidates.Add(ProductAssignment.ToCandidate(stub.Id, k));
    }
}
