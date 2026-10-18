using BuyList.Application.Options;
using Microsoft.Extensions.Options;

namespace BuyList.Application.Profit;

public sealed record ProfitInput(decimal SellPriceIncVat, decimal BuyPriceExVat, decimal BuyPriceIncVat, int? WeightGrams = null, decimal? KnownFbaFee = null);

/// <summary>Per-unit result. Percentages are in percent (52.94 = 52.94%). Money is rounded to pence.</summary>
public sealed record ProfitResult(
    decimal NetProfit,
    decimal RoiPercent,
    decimal MarginPercent,
    decimal MaxBuyPriceExVat,
    decimal MaxBuyPriceIncVat,
    ProfitBreakdown Breakdown);

/// <summary>Where the money goes, in the seller's actual cash terms (see <see cref="ProfitCalculator"/>).</summary>
public sealed record ProfitBreakdown(
    decimal NetRevenue,
    decimal OutputVat,
    decimal CostOfGoods,
    decimal ReferralFee,
    decimal FbaFee,
    decimal VatOnFees,
    decimal InboundCost,
    decimal ReturnsAllowance);

/// <summary>
/// Pure per-unit profit maths. Uses the 90-day average Buy Box price (supplied by the caller) as the sell price.
///
/// VAT registered:     revenue = sell / (1 + vat)   (output VAT goes to HMRC)
///                     cost of goods = buy ex VAT   (input VAT is reclaimed)
///                     Amazon fees and inbound cost are ex VAT (their VAT is reclaimed)
/// Not VAT registered: revenue = sell               (nothing to remit)
///                     cost of goods = buy inc VAT  (input VAT is a real cost)
///                     Amazon fees and inbound cost carry VAT as a cost
/// Returns allowance = configured % of revenue.
/// Investment (ROI denominator) = cost of goods + inbound cost.  Margin = profit / revenue.
/// </summary>
public class ProfitCalculator
{
    private readonly ProfitOptions _o;
    private readonly IFeeEstimator _fees;

    public ProfitCalculator(IOptions<ProfitOptions> options, IFeeEstimator fees)
    {
        _o = options.Value;
        _fees = fees;
    }

    public ProfitResult Calculate(ProfitInput input)
    {
        var c = Compute(input.SellPriceIncVat, input.WeightGrams, input.KnownFbaFee);
        var cogs = _o.VatRegistered ? input.BuyPriceExVat : input.BuyPriceIncVat;

        var profit = c.Revenue - cogs - c.Fees - c.Inbound - c.Returns;
        var investment = cogs + c.Inbound;
        var roi = investment > 0 ? profit / investment * 100m : 0m;
        var margin = c.Revenue > 0 ? profit / c.Revenue * 100m : 0m;

        var (maxEx, maxInc) = MaxBuy(c);

        var breakdown = new ProfitBreakdown(
            Money(c.Revenue), Money(c.OutputVat), Money(cogs), Money(c.Referral), Money(c.Fba),
            Money(c.VatOnFees), Money(c.Inbound), Money(c.Returns));

        return new ProfitResult(Money(profit), Pct(roi), Pct(margin), maxEx, maxInc, breakdown);
    }

    /// <summary>
    /// Highest cost of goods that still reaches the target ROI.
    /// Profit = A - I - cogs and ROI = profit / (cogs + I) >= T  =>  cogs <= A / (1 + T) - I,
    /// where A = revenue - fees - returns and I = inbound cost. Rounded DOWN to the penny.
    /// </summary>
    private (decimal ExVat, decimal IncVat) MaxBuy(Costs c)
    {
        var t = _o.TargetRoiPercent / 100m;
        var a = c.Revenue - c.Fees - c.Returns;
        var cogs = Math.Max(0m, a / (1m + t) - c.Inbound);

        var (ex, inc) = _o.VatRegistered ? (cogs, cogs * (1m + _o.VatRate)) : (cogs / (1m + _o.VatRate), cogs);
        return (Floor(ex), Floor(inc));
    }

    private Costs Compute(decimal sellIncVat, int? weightGrams, decimal? knownFbaFee)
    {
        var vat = 1m + _o.VatRate;
        var fee = _fees.Estimate(new FeeRequest(sellIncVat, weightGrams, knownFbaFee));

        var revenue = _o.VatRegistered ? sellIncVat / vat : sellIncVat;
        var outputVat = _o.VatRegistered ? sellIncVat - revenue : 0m;
        // Unregistered sellers can't reclaim VAT charged on Amazon fees and inbound costs, so it is a real cost.
        var vatOnFees = _o.VatRegistered ? 0m : (fee.ReferralFee + fee.FbaFee) * _o.VatRate;
        var inbound = _o.InboundCostPerUnit * (_o.VatRegistered ? 1m : vat);
        var returns = revenue * _o.ReturnsAllowancePercent / 100m;

        return new Costs(revenue, outputVat, fee.ReferralFee, fee.FbaFee, vatOnFees, inbound, returns);
    }

    private sealed record Costs(decimal Revenue, decimal OutputVat, decimal Referral, decimal Fba, decimal VatOnFees, decimal Inbound, decimal Returns)
    {
        public decimal Fees => Referral + Fba + VatOnFees;
    }

    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static decimal Pct(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static decimal Floor(decimal v) => Math.Floor(v * 100m) / 100m;
}
