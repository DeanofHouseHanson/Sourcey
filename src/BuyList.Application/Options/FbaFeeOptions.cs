namespace BuyList.Application.Options;

/// <summary>
/// APPROXIMATE FBA fee lookup used by the default fee estimator. The numbers shipped in appsettings.json are
/// placeholders, not Amazon's rate card: check them against Seller Central before trusting profit figures.
/// </summary>
public class FbaFeeOptions
{
    public const string SectionName = "Fba";

    /// <summary>Weight assumed when the product's weight is unknown.</summary>
    public int DefaultWeightGrams { get; set; } = 400;

    /// <summary>Fee tiers, ex VAT. The first tier whose MaxWeightGrams covers the weight applies.</summary>
    public List<FbaFeeTier> Tiers { get; set; } = new();
}

public class FbaFeeTier
{
    public string Name { get; set; } = string.Empty;
    public int MaxWeightGrams { get; set; }
    public decimal Fee { get; set; }
}
