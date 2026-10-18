using BuyList.Application.Options;
using BuyList.Application.Risk;
using BuyList.Domain.Enums;
using Microsoft.Extensions.Options;

namespace BuyList.Application.Ranking;

/// <param name="BuyCashPerUnit">Cash paid per unit at checkout (inc VAT); drives how many units the budget buys.</param>
/// <param name="MonthlySales">Keepa's monthly sales estimate for the whole listing; null if unknown.</param>
public sealed record ScoreInput(decimal NetProfit, decimal BuyCashPerUnit, int? MonthlySales, int FbaSellerCount);

public sealed record ScoreResult(
    RiskLevel RiskLevel,
    decimal RiskMultiplier,
    bool NoSalesData,
    decimal? MonthlyUnits,
    decimal EstMonthlyProfit,
    decimal RiskAdjustedMonthlyProfit,
    decimal? DaysToSell,
    int SuggestedQty);

/// <summary>
/// Ranking measures:
///   MonthlyUnits       = MonthlySales / (FbaSellerCount + 1)            (my share of the listing's sales)
///   EffectiveUnits     = min(MonthlyUnits, units the budget can buy)
///   EstMonthlyProfit   = NetProfit * EffectiveUnits
///   RiskAdjusted       = EstMonthlyProfit * RiskMultiplier
///   DaysToSell         = SuggestedQty / MonthlyUnits * 30
/// With no sales data nothing is guessed: EstMonthlyProfit is 0, DaysToSell is null, risk is VeryHigh, and the
/// suggested quantity is 1 (a test buy, if affordable). A confirmed 0 sales/month is real data: quantity 0.
/// </summary>
public class OpportunityScorer
{
    private readonly RankingOptions _o;
    private readonly RiskClassifier _risk;

    public OpportunityScorer(IOptions<RankingOptions> options, RiskClassifier risk)
    {
        _o = options.Value;
        _risk = risk;
    }

    public ScoreResult Score(ScoreInput input, decimal? budgetOverride = null)
    {
        var budget = budgetOverride ?? _o.BudgetPounds;
        var risk = _risk.Classify(input.MonthlySales);
        var affordable = input.BuyCashPerUnit > 0 ? (int)Math.Floor(budget / input.BuyCashPerUnit) : 0;

        if (risk.NoSalesData)
            return new ScoreResult(risk.Level, risk.Multiplier, true, null, 0m, 0m, null, Math.Min(1, affordable));

        var monthlyUnits = (decimal)input.MonthlySales!.Value / (Math.Max(0, input.FbaSellerCount) + 1);
        var effectiveUnits = Math.Min(monthlyUnits, affordable);
        var est = input.NetProfit * effectiveUnits;

        var qty = SuggestQty(monthlyUnits, affordable);
        decimal? days = monthlyUnits > 0 ? Math.Round(qty / monthlyUnits * 30m, 1) : null;

        return new ScoreResult(risk.Level, risk.Multiplier, false, monthlyUnits,
            Money(est), Money(est * risk.Multiplier), days, qty);
    }

    /// <summary>Enough to cover TargetDaysOfCover, at least 1, capped by MaxSuggestedQty and the budget.</summary>
    private int SuggestQty(decimal monthlyUnits, int affordable)
    {
        if (monthlyUnits <= 0) return 0; // confirmed zero sales: don't suggest buying
        var wanted = (int)Math.Floor(monthlyUnits * _o.TargetDaysOfCover / 30m);
        return Math.Max(0, Math.Min(Math.Max(1, wanted), Math.Min(_o.MaxSuggestedQty, affordable)));
    }

    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
