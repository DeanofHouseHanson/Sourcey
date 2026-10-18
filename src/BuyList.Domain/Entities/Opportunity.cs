using BuyList.Domain.Enums;

namespace BuyList.Domain.Entities;

public class Opportunity
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int SourceOfferId { get; set; }
    public SourceOffer? SourceOffer { get; set; }

    public decimal NetProfit { get; set; }
    public decimal RoiPercent { get; set; }
    public decimal MarginPercent { get; set; }
    public decimal MaxBuyPrice { get; set; }
    public decimal EstMonthlyProfit { get; set; }
    public RiskLevel RiskLevel { get; set; }
    public decimal RiskAdjustedMonthlyProfit { get; set; }

    /// <summary>Null when it cannot be computed (no sales data).</summary>
    public decimal? DaysToSell { get; set; }
    public int SuggestedQty { get; set; }
    public OpportunityStatus Status { get; set; } = OpportunityStatus.New;
    public OpportunityFlags Flags { get; set; }
    public DateTime CalculatedAt { get; set; }
}
