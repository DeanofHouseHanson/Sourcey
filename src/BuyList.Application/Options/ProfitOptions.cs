namespace BuyList.Application.Options;

public class ProfitOptions
{
    public const string SectionName = "Profit";

    public bool VatRegistered { get; set; } = true;
    public decimal VatRate { get; set; } = 0.20m;

    /// <summary>Inbound shipping + prep per unit, ex VAT.</summary>
    public decimal InboundCostPerUnit { get; set; } = 0.50m;

    public decimal ReturnsAllowancePercent { get; set; } = 2m;
    public decimal TargetRoiPercent { get; set; } = 30m;

    /// <summary>Approximate Amazon UK referral fee for the default estimator.</summary>
    public decimal ReferralFeePercent { get; set; } = 15m;
}
