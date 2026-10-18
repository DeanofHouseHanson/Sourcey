using BuyList.Application.Ranking;
using BuyList.Domain.Entities;

namespace BuyList.Tests.Application;

public class OpportunityRankerTests
{
    private static Opportunity Opp(int id, decimal est, decimal adjusted, decimal roi = 50m, decimal profit = 2m) => new()
    {
        Id = id, EstMonthlyProfit = est, RiskAdjustedMonthlyProfit = adjusted, RoiPercent = roi, NetProfit = profit
    };

    // A looks best raw (100) but is very risky (25 adjusted); C is the safest earner.
    private static readonly Opportunity[] Items = { Opp(1, 100, 25), Opp(2, 60, 48), Opp(3, 50, 50) };

    [Fact]
    public void Default_order_is_risk_adjusted_descending() =>
        Assert.Equal(new[] { 3, 2, 1 }, OpportunityRanker.Order(Items).Select(o => o.Id));

    [Fact]
    public void Raw_mode_orders_by_unadjusted_monthly_profit() =>
        Assert.Equal(new[] { 1, 2, 3 }, OpportunityRanker.Order(Items, RankingMode.Raw).Select(o => o.Id));

    [Fact]
    public void Ties_break_on_roi_then_net_profit_then_id()
    {
        var items = new[]
        {
            Opp(5, 10, 10, roi: 40, profit: 1),
            Opp(4, 10, 10, roi: 60, profit: 1),   // best ROI
            Opp(3, 10, 10, roi: 40, profit: 3),   // ties ROI with 5/2, more profit
            Opp(2, 10, 10, roi: 40, profit: 1),   // ties with 5 entirely -> lower id first
        };
        Assert.Equal(new[] { 4, 3, 2, 5 }, OpportunityRanker.Order(items).Select(o => o.Id));
    }

    [Fact]
    public void Negative_profit_sorts_last() =>
        Assert.Equal(new[] { 2, 1 }, OpportunityRanker.Order(new[] { Opp(1, -5, -5), Opp(2, 0, 0) }).Select(o => o.Id));

    [Fact]
    public void Ordering_does_not_mutate_the_input()
    {
        var copy = Items.ToArray();
        _ = OpportunityRanker.Order(Items).ToList();
        Assert.Equal(copy, Items);
    }
}
