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

    /// <summary>True when the FBA offer count wasn't available and the total "new" offer count was used instead (overstates competition).</summary>
    public bool FbaSellerCountIsApprox { get; set; }
    public bool AmazonOnListing { get; set; }

    /// <summary>Package weight in grams, if Keepa knows it.</summary>
    public int? WeightGrams { get; set; }

    /// <summary>Amazon's FBA pick-and-pack fee as reported by Keepa (ex VAT), if known; preferred over the approximate table.</summary>
    public decimal? KeepaFbaFee { get; set; }
}
