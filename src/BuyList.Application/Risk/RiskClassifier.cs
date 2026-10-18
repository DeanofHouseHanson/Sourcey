using BuyList.Application.Options;
using BuyList.Domain.Enums;
using Microsoft.Extensions.Options;

namespace BuyList.Application.Risk;

/// <summary>Maps estimated monthly sales to a risk level and multiplier using the configured bands.</summary>
public class RiskClassifier
{
    private readonly IReadOnlyList<RiskBand> _bands;

    public RiskClassifier(IOptions<RiskOptions> options)
    {
        var configured = options.Value.Bands;
        _bands = (configured.Count > 0 ? configured : RiskOptions.DefaultBands)
            .OrderBy(b => b.MinMonthlySales).ToList();
    }

    public RiskAssessment Classify(int? monthlySales)
    {
        // Missing (or nonsensical negative) data is VeryHigh risk and flagged, using that level's configured multiplier.
        if (monthlySales is null or < 0)
        {
            var veryHigh = _bands.FirstOrDefault(b => b.Level == RiskLevel.VeryHigh) ?? _bands[0];
            return new RiskAssessment(RiskLevel.VeryHigh, veryHigh.Multiplier, NoSalesData: true);
        }

        var band = _bands.LastOrDefault(b => b.MinMonthlySales <= monthlySales) ?? _bands[0];
        return new RiskAssessment(band.Level, band.Multiplier, NoSalesData: false);
    }

    /// <summary>"Max risk" filter: true if <paramref name="level"/> is no riskier than <paramref name="maxRisk"/>.</summary>
    public static bool IsWithin(RiskLevel level, RiskLevel maxRisk) => level >= maxRisk;
}
