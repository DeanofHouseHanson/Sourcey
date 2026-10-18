namespace BuyList.Domain.Entities;

/// <summary>
/// One of several ASINs Keepa returned for a product's EAN. Kept (with the market data from the same response) so the
/// owner can pick the right listing without spending more tokens.
/// </summary>
public class AsinCandidate
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public string Asin { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Brand { get; set; }
    public string? Mpn { get; set; }
    public string? ImageUrl { get; set; }
    public int? SalesRank { get; set; }
    public int? MonthlySales { get; set; }
    public decimal? Avg90BuyBox { get; set; }
    public int? FbaSellerCount { get; set; }
    public bool FbaSellerCountIsApprox { get; set; }
    public bool AmazonOnListing { get; set; }
    public int? WeightGrams { get; set; }
    public decimal? KeepaFbaFee { get; set; }
    public DateTime CapturedAt { get; set; }
}
