namespace BuyList.Domain.Entities;

/// <summary>Cached Keepa market data for a product at a point in time.</summary>
public class AmazonSnapshot
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public DateTime CapturedAt { get; set; }
    public decimal? Avg90BuyBox { get; set; }
    public int? SalesRank { get; set; }

    /// <summary>Null when Keepa has no estimate; never guessed.</summary>
    public int? MonthlySales { get; set; }
    public int FbaSellerCount { get; set; }
    public bool AmazonOnListing { get; set; }
}
