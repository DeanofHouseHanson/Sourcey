using BuyList.Application.Import;
using BuyList.Domain.Entities;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Infrastructure.Import;

/// <summary>
/// Matching rules, in order:
///  1. EAN/GTIN (canonical form) equals Product.Ean.
///  2. Exact brand (case-insensitive) + MPN (trimmed, case-insensitive). Refused when it would be ambiguous, or when the
///     product already has a DIFFERENT EAN (that is a variant, not the same item).
///  Title is never used. When an offer has a valid EAN but no product exists, a stub Product (EAN only plus whatever
///  the supplier gave) is created so the Keepa lookup can resolve its ASIN later.
///  Brand + MPN alone never creates a product (Keepa can't look those up), so such offers stay unmatched.
/// </summary>
public class ProductMatcher : IProductMatcher
{
    private readonly BuyListDbContext _db;
    public ProductMatcher(BuyListDbContext db) => _db = db;

    public async Task<MatchSummary> MatchPendingAsync(CancellationToken ct = default)
    {
        var pending = await _db.SourceOffers.Where(o => o.ProductId == null).OrderBy(o => o.Id).ToListAsync(ct);
        int byEan = 0, byBrandMpn = 0, created = 0, ambiguous = 0, noProduct = 0, unmatchable = 0;
        var eanCache = new Dictionary<string, Product>();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        foreach (var offer in pending)
        {
            var gtin = Gtin.NormaliseOrNull(offer.RawEan);
            var brand = Clean(offer.RawBrand);
            var mpn = Clean(offer.RawMpn);
            var hasBrandMpn = brand is not null && mpn is not null;

            if (gtin is null && !hasBrandMpn) { unmatchable++; continue; }

            // 1. EAN
            if (gtin is not null)
            {
                var byCode = eanCache.GetValueOrDefault(gtin)
                    ?? await _db.Products.Where(p => p.Ean == gtin).OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
                if (byCode is not null)
                {
                    eanCache[gtin] = byCode;
                    Assign(offer, byCode, gtin);
                    byEan++;
                    continue;
                }
            }

            // 2. Brand + MPN
            var brandLower = brand?.ToLowerInvariant();
            var mpnUpper = mpn?.ToUpperInvariant();
            var wasAmbiguous = false;
            if (hasBrandMpn)
            {
                var candidates = await _db.Products
                    .Where(p => p.Brand != null && p.Mpn != null
                                && p.Brand.Trim().ToLower() == brandLower
                                && p.Mpn.Trim().ToUpper() == mpnUpper)
                    .OrderBy(p => p.Id).Take(2).ToListAsync(ct);

                if (candidates.Count == 1)
                {
                    var p = candidates[0];
                    var conflictingEan = gtin is not null && p.Ean is not null && p.Ean != gtin;
                    if (!conflictingEan)
                    {
                        Assign(offer, p, gtin);
                        if (gtin is not null) eanCache[gtin] = p;
                        byBrandMpn++;
                        continue;
                    }
                    // Same brand+MPN but a different EAN: a different variant. Fall through to the EAN stub below.
                }
                else if (candidates.Count > 1) wasAmbiguous = true;
            }

            // 3. New product from a valid EAN
            if (gtin is not null)
            {
                var stub = new Product { Ean = gtin, Brand = brand, Mpn = mpn, Title = Clean(offer.RawTitle) };
                _db.Products.Add(stub);
                await _db.SaveChangesAsync(ct);
                eanCache[gtin] = stub;
                offer.ProductId = stub.Id;
                created++;
                continue;
            }

            if (wasAmbiguous) ambiguous++; else noProduct++;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new MatchSummary(byEan, byBrandMpn, created, ambiguous, noProduct, unmatchable);
    }

    private static void Assign(SourceOffer offer, Product product, string? gtin)
    {
        offer.ProductId = product.Id;
        // Learn identifiers the product was missing; never overwrite existing ones.
        if (product.Ean is null && gtin is not null) product.Ean = gtin;
        product.Brand ??= Clean(offer.RawBrand);
        product.Mpn ??= Clean(offer.RawMpn);
        product.Title ??= Clean(offer.RawTitle);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
