namespace BuyList.Application.Profit;

/// <param name="SellPriceIncVat">Customer-facing price (Amazon UK prices include VAT).</param>
/// <param name="WeightGrams">Null when unknown; the estimator applies its own default.</param>
/// <param name="KnownFbaFee">An FBA fee (ex VAT) already known from Keepa; estimators should prefer it over any table.</param>
public sealed record FeeRequest(decimal SellPriceIncVat, int? WeightGrams = null, decimal? KnownFbaFee = null);

/// <summary>Amazon fees, both ex VAT (Amazon charges VAT on top of its fees).</summary>
public sealed record FeeEstimate(decimal ReferralFee, decimal FbaFee, string Basis);

/// <summary>
/// Estimates Amazon fees for a sale. The default implementation is a configurable approximation;
/// an SP-API (Product Fees) implementation can replace it via DI without touching the calculator.
/// </summary>
public interface IFeeEstimator
{
    FeeEstimate Estimate(FeeRequest request);
}
