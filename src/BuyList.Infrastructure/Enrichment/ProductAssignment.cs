using BuyList.Application.Keepa;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Infrastructure.Enrichment;

/// <summary>Writing a Keepa product onto our Product / AmazonSnapshot rows. Shared by enrichment and by manual ASIN choice.</summary>
internal static class ProductAssignment
{
    /// <summary>
    /// Attach the ASIN to the stub. If a product with that ASIN already exists (e.g. it was matched earlier by brand + MPN),
    /// fold the stub into it instead, since ASIN is unique. Returns the product that now owns the ASIN.
    /// </summary>
    public static async Task<Product> ResolveAsync(BuyListDbContext db, Product stub, KeepaProduct k, CancellationToken ct)
    {
        var existing = await db.Products.FirstOrDefaultAsync(p => p.Asin == k.Asin && p.Id != stub.Id, ct);
        if (existing is null)
        {
            stub.Asin = k.Asin;
            stub.AsinLookupStatus = AsinLookupStatus.Resolved;
            ApplyProductData(stub, k);
            return stub;
        }

        foreach (var offer in await db.SourceOffers.Where(o => o.ProductId == stub.Id).ToListAsync(ct))
            offer.ProductId = existing.Id;
        existing.Ean ??= stub.Ean;
        existing.KeepaCheckedAt = stub.KeepaCheckedAt;
        existing.AsinLookupStatus = AsinLookupStatus.Resolved;
        db.Products.Remove(stub);
        ApplyProductData(existing, k);
        return existing;
    }

    public static void ApplyProductData(Product p, KeepaProduct k)
    {
        p.Title = k.Title ?? p.Title;          // Amazon's title is the authoritative one
        p.Brand ??= k.Brand;
        p.Mpn ??= k.Mpn;
        p.ImageUrl = k.ImageUrl ?? p.ImageUrl;
        if (p.Ean is null && k.Eans.Count == 1) p.Ean = k.Eans[0];
    }

    public static void AddSnapshot(BuyListDbContext db, Product p, KeepaProduct k) =>
        db.AmazonSnapshots.Add(new AmazonSnapshot
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

    public static AsinCandidate ToCandidate(int productId, KeepaProduct k) => new()
    {
        ProductId = productId, Asin = k.Asin, Title = k.Title, Brand = k.Brand, Mpn = k.Mpn, ImageUrl = k.ImageUrl,
        SalesRank = k.SalesRankCurrent, MonthlySales = k.MonthlySold, Avg90BuyBox = k.Avg90BuyBox,
        FbaSellerCount = k.FbaSellerCount, FbaSellerCountIsApprox = k.FbaSellerCountIsApprox || k.FbaSellerCount is null,
        AmazonOnListing = k.AmazonOnListing, WeightGrams = k.WeightGrams, KeepaFbaFee = k.FbaPickAndPackFee, CapturedAt = k.FetchedAt
    };

    public static KeepaProduct ToKeepaProduct(AsinCandidate c, string? ean) => new(
        c.Asin, c.Title, c.Brand, c.ImageUrl, c.Mpn, ean is null ? Array.Empty<string>() : new[] { ean },
        c.Avg90BuyBox, c.SalesRank, null, null, c.FbaSellerCount, c.FbaSellerCountIsApprox, c.AmazonOnListing,
        c.MonthlySales, c.WeightGrams, c.KeepaFbaFee, c.CapturedAt);
}
