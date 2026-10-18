using BuyList.Domain.Enums;

namespace BuyList.Application.Options;

public class RiskOptions
{
    public const string SectionName = "Risk";

    /// <summary>Configured in appsettings.json; bands are matched on MinMonthlySales (highest matching band wins).</summary>
    public List<RiskBand> Bands { get; set; } = new();

    /// <summary>Used when no bands are configured, so a missing config section never silently disables risk.</summary>
    public static IReadOnlyList<RiskBand> DefaultBands { get; } = new[]
    {
        new RiskBand { MinMonthlySales = 0,   Level = RiskLevel.VeryHigh, Multiplier = 0.25m },
        new RiskBand { MinMonthlySales = 10,  Level = RiskLevel.High,     Multiplier = 0.5m },
        new RiskBand { MinMonthlySales = 30,  Level = RiskLevel.Medium,   Multiplier = 0.8m },
        new RiskBand { MinMonthlySales = 100, Level = RiskLevel.Low,      Multiplier = 1.0m },
        new RiskBand { MinMonthlySales = 300, Level = RiskLevel.VeryLow,  Multiplier = 1.0m },
    };
}

public class RiskBand
{
    public int MinMonthlySales { get; set; }
    public RiskLevel Level { get; set; }
    public decimal Multiplier { get; set; }
}
