using BuyList.Application.Import;
using BuyList.Domain.Entities;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Infrastructure.Import;

public class OfferImportService : IOfferImportService
{
    private const int ChunkSize = 400; // keep IN (...) lists well under SQLite's parameter limit

    private readonly BuyListDbContext _db;
    private readonly IProductMatcher _matcher;
    private readonly TimeProvider _time;

    public OfferImportService(BuyListDbContext db, IProductMatcher matcher, TimeProvider time)
    {
        _db = db;
        _matcher = matcher;
        _time = time;
    }

    public async Task<ImportSummary> ImportAsync(IReadOnlyList<ValidOffer> offers, int rejectedCount, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        int added = 0, updated = 0, refreshed = 0;

        var existing = new Dictionary<string, SourceOffer>(StringComparer.Ordinal);
        foreach (var chunk in offers.Select(o => o.SourceUrl).Distinct().Chunk(ChunkSize))
            foreach (var e in await _db.SourceOffers.Where(o => chunk.Contains(o.SourceUrl)).ToListAsync(ct))
                existing[e.SourceUrl] = e;

        foreach (var o in offers)
        {
            if (!existing.TryGetValue(o.SourceUrl, out var entity))
            {
                _db.SourceOffers.Add(new SourceOffer
                {
                    RetailerName = o.Retailer, SourceUrl = o.SourceUrl,
                    BuyPriceExVat = o.BuyPriceExVat, BuyPriceIncVat = o.BuyPriceIncVat,
                    StockStatus = o.Stock, CheckedAt = now,
                    RawEan = o.RawEan, RawMpn = o.Mpn, RawBrand = o.Brand, RawTitle = o.Title
                });
                added++;
                continue;
            }

            var identityChanged = entity.RawEan != o.RawEan || entity.RawMpn != o.Mpn || entity.RawBrand != o.Brand;
            var changed = identityChanged
                || entity.RetailerName != o.Retailer || entity.BuyPriceExVat != o.BuyPriceExVat
                || entity.BuyPriceIncVat != o.BuyPriceIncVat || entity.StockStatus != o.Stock
                || entity.RawTitle != o.Title;

            entity.RetailerName = o.Retailer;
            entity.BuyPriceExVat = o.BuyPriceExVat;
            entity.BuyPriceIncVat = o.BuyPriceIncVat;
            entity.StockStatus = o.Stock;
            entity.RawEan = o.RawEan; entity.RawMpn = o.Mpn; entity.RawBrand = o.Brand; entity.RawTitle = o.Title;
            entity.CheckedAt = now;                    // re-importing a price re-confirms it
            if (identityChanged) entity.ProductId = null; // identifiers changed: match again from scratch

            if (changed) updated++; else refreshed++;
        }

        await _db.SaveChangesAsync(ct);
        var match = await _matcher.MatchPendingAsync(ct);
        return new ImportSummary(added, updated, refreshed, rejectedCount, match);
    }
}
