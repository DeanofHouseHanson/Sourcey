using BuyList.Application.Options;
using BuyList.Application.Profit;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Application;

public class ApproximateFeeEstimatorTests
{
    private static ApproximateFeeEstimator Make(params FbaFeeTier[] tiers)
    {
        var fba = new FbaFeeOptions { DefaultWeightGrams = 400 };
        fba.Tiers.AddRange(tiers);
        return new ApproximateFeeEstimator(Options.Create(new ProfitOptions { ReferralFeePercent = 15m }), Options.Create(fba));
    }

    private static readonly FbaFeeTier[] Tiers =
    {
        new() { Name = "100g", MaxWeightGrams = 100, Fee = 1.80m },
        new() { Name = "500g", MaxWeightGrams = 500, Fee = 2.90m },
        new() { Name = "1kg", MaxWeightGrams = 1000, Fee = 3.40m },
    };

    [Fact]
    public void Referral_is_a_percentage_of_the_vat_inclusive_price() =>
        Assert.Equal(3.60m, Make(Tiers).Estimate(new FeeRequest(24.00m)).ReferralFee);

    [Theory]
    [InlineData(1, 1.80)]
    [InlineData(100, 1.80)]   // boundary is inclusive
    [InlineData(101, 2.90)]
    [InlineData(500, 2.90)]
    [InlineData(501, 3.40)]
    [InlineData(1000, 3.40)]
    public void Fba_fee_comes_from_the_matching_weight_tier(int grams, double fee) =>
        Assert.Equal((decimal)fee, Make(Tiers).Estimate(new FeeRequest(10m, grams)).FbaFee);

    [Fact]
    public void Unknown_weight_uses_the_default_weight() =>
        Assert.Equal(2.90m, Make(Tiers).Estimate(new FeeRequest(10m)).FbaFee); // 400g -> 500g tier

    [Fact]
    public void Tiers_can_be_configured_in_any_order() =>
        Assert.Equal(1.80m, Make(Tiers.Reverse().ToArray()).Estimate(new FeeRequest(10m, 50)).FbaFee);

    [Fact]
    public void Heavier_than_every_tier_uses_the_top_tier() =>
        Assert.Equal(3.40m, Make(Tiers).Estimate(new FeeRequest(10m, 50_000)).FbaFee);

    [Fact]
    public void Missing_tiers_is_a_clear_error() =>
        Assert.Throws<InvalidOperationException>(() => Make().Estimate(new FeeRequest(10m)));

    [Fact]
    public void Result_is_labelled_as_an_approximation() =>
        Assert.StartsWith("approx", Make(Tiers).Estimate(new FeeRequest(10m)).Basis);

    [Fact]
    public void A_fba_fee_known_from_keepa_overrides_the_weight_table()
    {
        var e = Make(Tiers).Estimate(new FeeRequest(24.00m, WeightGrams: 50, KnownFbaFee: 2.89m));
        Assert.Equal(2.89m, e.FbaFee);                 // not the 100g tier's 1.80
        Assert.Equal(3.60m, e.ReferralFee);            // referral is still the configured percentage
        Assert.Contains("Keepa", e.Basis);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void A_missing_or_zero_known_fee_falls_back_to_the_table(double? known) =>
        Assert.Equal(1.80m, Make(Tiers).Estimate(new FeeRequest(10m, 50, known is null ? null : (decimal)known)).FbaFee);
}
