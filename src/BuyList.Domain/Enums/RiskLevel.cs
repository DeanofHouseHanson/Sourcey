namespace BuyList.Domain.Enums;

/// <summary>Ordered from riskiest to safest so that "max risk" filters can compare values.</summary>
public enum RiskLevel
{
    VeryHigh = 0,
    High = 1,
    Medium = 2,
    Low = 3,
    VeryLow = 4
}
