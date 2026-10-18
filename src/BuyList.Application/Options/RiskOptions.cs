using BuyList.Domain.Enums;

namespace BuyList.Application.Options;

public class RiskOptions
{
    public const string SectionName = "Risk";

    /// <summary>Configured in appsettings.json; bands are matched on MinMonthlySales (highest matching band wins).</summary>
    public List<RiskBand> Bands { get; set; } = new();
}

public class RiskBand
{
    public int MinMonthlySales { get; set; }
    public RiskLevel Level { get; set; }
    public decimal Multiplier { get; set; }
}
