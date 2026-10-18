using BuyList.Application.Options;
using BuyList.Application.Profit;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Application;

/// <summary>Fixed fees so expected values can be checked by hand.</summary>
internal sealed class StubFees : IFeeEstimator
{
    private readonly decimal _referral, _fba;
    public StubFees(decimal referral = 3.60m, decimal fba = 3.00m) { _referral = referral; _fba = fba; }
    public FeeEstimate Estimate(FeeRequest request) => new(_referral, _fba, "stub");
}

public class ProfitCalculatorTests
{
    private static ProfitCalculator Make(bool vatRegistered = true, IFeeEstimator? fees = null, decimal targetRoi = 30m) =>
        new(Options.Create(new ProfitOptions
        {
            VatRegistered = vatRegistered, VatRate = 0.20m, InboundCostPerUnit = 0.50m,
            ReturnsAllowancePercent = 2m, TargetRoiPercent = targetRoi, ReferralFeePercent = 15m
        }), fees ?? new StubFees());

    // Worked example A - VAT registered. Sell (90d avg Buy Box) £24.00 inc VAT, buy £8.00 ex VAT (£9.60 inc).
    //   revenue        = 24.00 / 1.2            = 20.00   (output VAT 4.00 goes to HMRC)
    //   cost of goods  = 8.00                              (input VAT reclaimed)
    //   referral 3.60 (15% of 24.00), FBA 3.00, inbound 0.50, returns 2% of 20.00 = 0.40
    //   profit         = 20 - 8 - 3.60 - 3.00 - 0.50 - 0.40 = 4.50
    //   ROI            = 4.50 / (8.00 + 0.50)   = 52.94%
    //   margin         = 4.50 / 20.00           = 22.50%
    //   max buy @30%   : A = 20 - 6.60 - 0.40 = 13.00; 13.00/1.3 - 0.50 = 9.50 ex VAT (11.40 inc)
    [Fact]
    public void Registered_worked_example()
    {
        var r = Make().Calculate(new ProfitInput(24.00m, 8.00m, 9.60m));

        Assert.Equal(4.50m, r.NetProfit);
        Assert.Equal(52.94m, r.RoiPercent);
        Assert.Equal(22.50m, r.MarginPercent);
        Assert.Equal(9.50m, r.MaxBuyPriceExVat);
        Assert.Equal(11.40m, r.MaxBuyPriceIncVat);

        var b = r.Breakdown;
        Assert.Equal(20.00m, b.NetRevenue);
        Assert.Equal(4.00m, b.OutputVat);
        Assert.Equal(8.00m, b.CostOfGoods);
        Assert.Equal(3.60m, b.ReferralFee);
        Assert.Equal(3.00m, b.FbaFee);
        Assert.Equal(0m, b.VatOnFees);
        Assert.Equal(0.50m, b.InboundCost);
        Assert.Equal(0.40m, b.ReturnsAllowance);
    }

    // Worked example B - NOT VAT registered, same product.
    //   revenue = 24.00 (no VAT to remit); cost of goods = 9.60 (input VAT is a cost)
    //   VAT on Amazon fees = (3.60 + 3.00) * 0.2 = 1.32; inbound = 0.50 * 1.2 = 0.60; returns = 2% of 24 = 0.48
    //   profit = 24 - 9.60 - 6.60 - 1.32 - 0.60 - 0.48 = 5.40;  ROI = 5.40 / (9.60 + 0.60) = 52.94%
    //   max buy: A = 24 - 7.92 - 0.48 = 15.60; 15.60/1.3 - 0.60 = 11.40 inc VAT (9.50 ex)
    [Fact]
    public void Unregistered_worked_example()
    {
        var r = Make(vatRegistered: false).Calculate(new ProfitInput(24.00m, 8.00m, 9.60m));

        Assert.Equal(5.40m, r.NetProfit);
        Assert.Equal(52.94m, r.RoiPercent);
        Assert.Equal(22.50m, r.MarginPercent);
        Assert.Equal(11.40m, r.MaxBuyPriceIncVat);
        Assert.Equal(9.50m, r.MaxBuyPriceExVat);
        Assert.Equal(0m, r.Breakdown.OutputVat);
        Assert.Equal(1.32m, r.Breakdown.VatOnFees);
        Assert.Equal(9.60m, r.Breakdown.CostOfGoods);
    }

    // Loss-making: sell £10.00 inc VAT, fees 1.50 + 3.00 -> revenue 8.3333
    //   profit = 8.3333 - 8 - 4.50 - 0.50 - 0.1667 = -4.8333;  ROI = -4.8333/8.5 = -56.86%;  margin = -58.00%
    //   max buy: A = 8.3333 - 4.50 - 0.1667 = 3.6667; 3.6667/1.3 - 0.5 = 2.3205 -> 2.32 ex; 2.7846 -> 2.78 inc
    [Fact]
    public void Loss_making_item_has_negative_profit_and_low_max_buy()
    {
        var r = Make(fees: new StubFees(1.50m, 3.00m)).Calculate(new ProfitInput(10.00m, 8.00m, 9.60m));

        Assert.Equal(-4.83m, r.NetProfit);
        Assert.Equal(-56.86m, r.RoiPercent);
        Assert.Equal(-58.00m, r.MarginPercent);
        Assert.Equal(2.32m, r.MaxBuyPriceExVat);
        Assert.Equal(2.78m, r.MaxBuyPriceIncVat);
    }

    [Fact]
    public void Max_buy_never_goes_negative_when_fees_swallow_the_price()
    {
        var r = Make().Calculate(new ProfitInput(4.00m, 1.00m, 1.20m));
        Assert.Equal(0m, r.MaxBuyPriceExVat);
        Assert.Equal(0m, r.MaxBuyPriceIncVat);
    }

    [Theory]
    [InlineData(true, 24.00, 30)]
    [InlineData(true, 15.99, 30)]
    [InlineData(true, 59.50, 45)]
    [InlineData(true, 18.50, 20)]
    [InlineData(false, 24.00, 30)]
    [InlineData(false, 33.33, 50)]
    public void Paying_the_max_buy_price_achieves_at_least_the_target_roi(bool registered, double sell, int target)
    {
        var calc = Make(registered, targetRoi: target);
        var max = calc.Calculate(new ProfitInput((decimal)sell, 1m, 1.2m));
        Assert.True(max.MaxBuyPriceExVat > 0);

        var atMax = calc.Calculate(new ProfitInput((decimal)sell, max.MaxBuyPriceExVat, max.MaxBuyPriceIncVat));
        Assert.InRange(atMax.RoiPercent, target, target + 1.0m); // floored to the penny, so just above target

        // and a penny more would miss it
        var over = calc.Calculate(new ProfitInput((decimal)sell, max.MaxBuyPriceExVat + 0.02m, max.MaxBuyPriceIncVat + 0.024m));
        Assert.True(over.RoiPercent < target);
    }

    [Fact]
    public void Zero_buy_price_and_zero_sell_price_do_not_divide_by_zero()
    {
        var r = Make(fees: new StubFees(0, 0)).Calculate(new ProfitInput(0m, 0m, 0m));
        Assert.Equal(0m, r.MarginPercent);
        Assert.Equal(-0.50m, r.NetProfit); // just the inbound cost
    }

    [Fact]
    public void Works_with_the_default_approximate_estimator()
    {
        // 15% referral on 24.00 = 3.60; no weight -> 400g -> "Up to 500g" tier = 2.90
        // profit = 20 - 8 - 3.60 - 2.90 - 0.50 - 0.40 = 4.60;  ROI = 4.60 / 8.50 = 54.12%
        var estimator = new ApproximateFeeEstimator(
            Options.Create(new ProfitOptions { ReferralFeePercent = 15m }),
            Options.Create(new FbaFeeOptions
            {
                DefaultWeightGrams = 400,
                Tiers = { new FbaFeeTier { Name = "Up to 500g", MaxWeightGrams = 500, Fee = 2.90m } }
            }));
        var r = Make(fees: estimator).Calculate(new ProfitInput(24.00m, 8.00m, 9.60m));

        Assert.Equal(4.60m, r.NetProfit);
        Assert.Equal(54.12m, r.RoiPercent);
    }
}
