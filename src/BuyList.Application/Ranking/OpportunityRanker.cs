using BuyList.Domain.Entities;

namespace BuyList.Application.Ranking;

public enum RankingMode
{
    RiskAdjusted = 0,
    Raw = 1
}

/// <summary>Default ordering of the Buy List: highest monthly profit first (risk-adjusted unless Raw is chosen).</summary>
public static class OpportunityRanker
{
    public static IOrderedEnumerable<Opportunity> Order(IEnumerable<Opportunity> items, RankingMode mode = RankingMode.RiskAdjusted)
    {
        var ordered = mode == RankingMode.Raw
            ? items.OrderByDescending(o => o.EstMonthlyProfit)
            : items.OrderByDescending(o => o.RiskAdjustedMonthlyProfit);

        // Deterministic tie-breaks: better ROI, then more profit per unit, then oldest row.
        return ordered.ThenByDescending(o => o.RoiPercent)
                      .ThenByDescending(o => o.NetProfit)
                      .ThenBy(o => o.Id);
    }
}
