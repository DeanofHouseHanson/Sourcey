using BuyList.Application.Options;
using BuyList.Application.Ranking;
using BuyList.Application.Risk;
using BuyList.Domain.Enums;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Application;

public class OpportunityScorerTests
{
    private static OpportunityScorer Make(decimal budget = 500m) => new(
        Options.Create(new RankingOptions { BudgetPounds = budget, TargetDaysOfCover = 30, MaxSuggestedQty = 50 }),
        new RiskClassifier(Options.Create(new RiskOptions())));

    // 60 sales/month, 2 FBA sellers -> my share = 60 / 3 = 20 units/month. Net £3.00/unit, costs £10 cash/unit.
    //   affordable = 500/10 = 50 -> effective 20 units; est = 3 * 20 = 60.00; Medium risk x0.8 = 48.00
    //   qty = 20 (30 days of cover); days to sell = 20 / 20 * 30 = 30.0
    [Fact]
    public void Worked_example()
    {
        var s = Make().Score(new ScoreInput(3.00m, 10m, 60, 2));

        Assert.Equal(20m, s.MonthlyUnits);
        Assert.Equal(60.00m, s.EstMonthlyProfit);
        Assert.Equal(RiskLevel.Medium, s.RiskLevel);
        Assert.Equal(48.00m, s.RiskAdjustedMonthlyProfit);
        Assert.Equal(20, s.SuggestedQty);
        Assert.Equal(30.0m, s.DaysToSell);
        Assert.False(s.NoSalesData);
    }

    [Fact]
    public void More_fba_sellers_means_a_smaller_share()
    {
        // 90 / (8 + 1) = 10 units
        Assert.Equal(10m, Make().Score(new ScoreInput(1m, 5m, 90, 8)).MonthlyUnits);
    }

    // 300 sales, no FBA competitors -> 300 units/month, but £500 / £10 only buys 50.
    //   est = 2 * 50 = 100.00 (VeryLow x1.0); qty capped at 50; days = 50 / 300 * 30 = 5.0
    [Fact]
    public void Units_are_capped_by_what_the_budget_can_buy()
    {
        var s = Make().Score(new ScoreInput(2m, 10m, 300, 0));
        Assert.Equal(100.00m, s.EstMonthlyProfit);
        Assert.Equal(100.00m, s.RiskAdjustedMonthlyProfit);
        Assert.Equal(50, s.SuggestedQty);
        Assert.Equal(5.0m, s.DaysToSell);
    }

    [Fact]
    public void Budget_override_is_used_for_budget_mode()
    {
        // £95 / £10 = 9 units; 100 sales/month (Low, x1.0) -> est = 2 * 9 = 18.00; qty 9
        var s = Make().Score(new ScoreInput(2m, 10m, 100, 0), budgetOverride: 95m);
        Assert.Equal(18.00m, s.EstMonthlyProfit);
        Assert.Equal(9, s.SuggestedQty);
    }

    [Fact]
    public void Missing_sales_data_is_not_guessed()
    {
        var s = Make().Score(new ScoreInput(5m, 10m, null, 1));

        Assert.True(s.NoSalesData);
        Assert.Equal(RiskLevel.VeryHigh, s.RiskLevel);
        Assert.Equal(0.25m, s.RiskMultiplier);
        Assert.Null(s.MonthlyUnits);
        Assert.Null(s.DaysToSell);
        Assert.Equal(0m, s.EstMonthlyProfit);
        Assert.Equal(0m, s.RiskAdjustedMonthlyProfit);
        Assert.Equal(1, s.SuggestedQty); // a single test unit
    }

    [Fact]
    public void Missing_sales_data_suggests_nothing_if_one_unit_is_unaffordable() =>
        Assert.Equal(0, Make(budget: 5m).Score(new ScoreInput(5m, 10m, null, 0)).SuggestedQty);

    [Fact]
    public void Confirmed_zero_sales_is_very_high_risk_but_not_flagged_and_suggests_nothing()
    {
        var s = Make().Score(new ScoreInput(5m, 10m, 0, 0));
        Assert.False(s.NoSalesData);
        Assert.Equal(RiskLevel.VeryHigh, s.RiskLevel);
        Assert.Equal(0m, s.EstMonthlyProfit);
        Assert.Null(s.DaysToSell);
        Assert.Equal(0, s.SuggestedQty);
    }

    [Fact]
    public void Slow_sellers_still_get_a_quantity_of_at_least_one()
    {
        // 20 sales / 3 = 6.67 units per month -> 6 for 30 days; 5 sales / 1 = 5; 1 sale / 2 -> 0.33 -> min 1
        var s = Make().Score(new ScoreInput(4m, 10m, 1, 1));
        Assert.Equal(1, s.SuggestedQty);
        Assert.Equal(60.0m, s.DaysToSell); // 1 / 0.5 * 30
    }

    [Fact]
    public void Negative_profit_flows_through_as_negative_monthly_profit()
    {
        var s = Make().Score(new ScoreInput(-2m, 10m, 100, 0)); // 100 units, 50 affordable
        Assert.Equal(-100.00m, s.EstMonthlyProfit);
        Assert.Equal(-100.00m, s.RiskAdjustedMonthlyProfit); // Low risk, x1.0
    }

    [Fact]
    public void Risk_multiplier_scales_the_adjusted_figure()
    {
        // 5 sales -> VeryHigh x0.25; units 5, est = 10 * 5 = 50.00, adjusted 12.50
        var s = Make().Score(new ScoreInput(10m, 10m, 5, 0));
        Assert.Equal(50.00m, s.EstMonthlyProfit);
        Assert.Equal(12.50m, s.RiskAdjustedMonthlyProfit);
    }

    [Fact]
    public void Negative_seller_count_is_treated_as_zero() =>
        Assert.Equal(40m, Make().Score(new ScoreInput(1m, 5m, 40, -3)).MonthlyUnits);
}
