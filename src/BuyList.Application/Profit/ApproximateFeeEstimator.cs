using BuyList.Application.Options;
using Microsoft.Extensions.Options;

namespace BuyList.Application.Profit;

/// <summary>
/// APPROXIMATION: referral fee = a flat percentage of the VAT-inclusive price; FBA fee = weight-tier lookup.
/// Ignores category-specific referral rates, minimum referral fees, dimensional weight and size tiers.
/// </summary>
public class ApproximateFeeEstimator : IFeeEstimator
{
    private readonly ProfitOptions _profit;
    private readonly FbaFeeOptions _fba;

    public ApproximateFeeEstimator(IOptions<ProfitOptions> profit, IOptions<FbaFeeOptions> fba)
    {
        _profit = profit.Value;
        _fba = fba.Value;
    }

    public FeeEstimate Estimate(FeeRequest request)
    {
        var referral = request.SellPriceIncVat * _profit.ReferralFeePercent / 100m;
        if (request.KnownFbaFee is > 0)
            return new FeeEstimate(referral, request.KnownFbaFee.Value,
                $"approx: {_profit.ReferralFeePercent}% referral, FBA fee reported by Keepa");

        var weight = request.WeightGrams ?? _fba.DefaultWeightGrams;

        var tiers = _fba.Tiers.OrderBy(t => t.MaxWeightGrams).ToList();
        if (tiers.Count == 0)
            throw new InvalidOperationException("No FBA fee tiers configured (section 'Fba:Tiers').");

        // Heavier than the top tier: charge the top tier's fee (still an approximation).
        var tier = tiers.FirstOrDefault(t => weight <= t.MaxWeightGrams) ?? tiers[^1];
        return new FeeEstimate(referral, tier.Fee, $"approx: {_profit.ReferralFeePercent}% referral, FBA tier '{tier.Name}'");
    }
}
