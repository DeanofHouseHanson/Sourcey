using BuyList.Domain.Enums;

namespace BuyList.Web.Display;

public static class RiskBadge
{
    public static string Label(RiskLevel level) => level switch
    {
        RiskLevel.VeryHigh => "Very high",
        RiskLevel.High => "High",
        RiskLevel.Medium => "Medium",
        RiskLevel.Low => "Low",
        _ => "Very low"
    };

    /// <summary>CSS class from site.css (red to green). The text label is always shown too, so colour isn't the only signal.</summary>
    public static string Css(RiskLevel level) => "risk risk-" + level;
}
