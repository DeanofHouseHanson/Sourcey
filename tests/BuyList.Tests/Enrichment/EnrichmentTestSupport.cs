using BuyList.Application.Keepa;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;

namespace BuyList.Tests.Enrichment;

/// <summary>In-memory IKeepaClient that records every call and honours the token cap at a flat 3 tokens per item.</summary>
public sealed class ScriptedKeepa : IKeepaClient
{
    public List<KeepaProduct> Catalogue { get; } = new();
    public List<(string Kind, IReadOnlyList<string> Items, int MaxTokens)> Calls { get; } = new();
    public int? TokensLeft { get; set; } = 1000;
    public bool Throttle { get; set; }
    public Exception? FailStatusWith { get; set; }
    public Func<Task>? OnStatus { get; set; }

    public async Task<KeepaTokenStatus> GetTokenStatusAsync(CancellationToken ct = default)
    {
        if (OnStatus is not null) await OnStatus();
        if (FailStatusWith is not null) throw FailStatusWith;
        return new KeepaTokenStatus(TokensLeft, 20, 30000);
    }

    public Task<KeepaFetchResult> GetByAsinsAsync(IReadOnlyList<string> asins, int maxTokens, CancellationToken ct = default) =>
        Fetch("asin", asins, maxTokens, p => asins.Contains(p.Asin));

    public Task<KeepaFetchResult> GetByCodesAsync(IReadOnlyList<string> codes, int maxTokens, CancellationToken ct = default) =>
        Fetch("code", codes, maxTokens, p => p.Eans.Any(codes.Contains));

    private Task<KeepaFetchResult> Fetch(string kind, IReadOnlyList<string> items, int maxTokens, Func<KeepaProduct, bool> match)
    {
        Calls.Add((kind, items.ToList(), maxTokens));
        if (Throttle) return Task.FromResult(new KeepaFetchResult(Array.Empty<KeepaProduct>(), items, 0, new KeepaTokenStatus(0, 20, 30000), true));

        var count = Math.Min(items.Count, Math.Max(0, maxTokens) / 3);
        var sent = items.Take(count).ToList();
        var products = Catalogue.Where(p => match(p) && (kind == "asin" ? sent.Contains(p.Asin) : p.Eans.Any(sent.Contains))).ToList();
        return Task.FromResult(new KeepaFetchResult(products, items.Skip(count).ToList(), count * 3,
            new KeepaTokenStatus((TokensLeft ?? 0) - count * 3, 20, 30000), false));
    }

    public static KeepaProduct Kp(string asin, string ean, decimal? price = 24.00m, int? sold = 60, int? fba = 2, bool amazon = false,
        int? weight = 420, decimal? fee = 3.00m, bool approx = false, string? title = "Amazon Title") =>
        new(asin, title, "Acme", null, "M-1", new[] { ean }, price, 1234, 1500, 40, fba, approx, amazon, sold, weight, fee,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
}

public static class Seed
{
    public static int Counter;

    public static Product Stub(BuyListDbContext db, string ean, StockStatus stock = StockStatus.InStock, string? asin = null, DateTime? checkedAt = null)
    {
        var p = new Product { Ean = ean, Asin = asin, Title = "Supplier title", KeepaCheckedAt = checkedAt };
        db.Products.Add(p);
        db.SourceOffers.Add(Offer(p, stock));
        db.SaveChanges();
        return p;
    }

    public static SourceOffer OfferFor(int productId, string retailer, decimal ex = 8.00m, decimal inc = 9.60m) => new()
    {
        ProductId = productId, RetailerName = retailer, SourceUrl = $"https://shop.example/p/{++Counter}", BuyPriceExVat = ex,
        BuyPriceIncVat = inc, StockStatus = StockStatus.InStock, CheckedAt = DateTime.UtcNow
    };

    public static SourceOffer Offer(Product p, StockStatus stock = StockStatus.InStock, decimal ex = 8.00m, decimal inc = 9.60m, string retailer = "Shop") => new()
    {
        Product = p, RetailerName = retailer, SourceUrl = $"https://shop.example/p/{++Counter}", BuyPriceExVat = ex, BuyPriceIncVat = inc,
        StockStatus = stock, CheckedAt = DateTime.UtcNow
    };
}
