using BuyList.Application.Options;
using BuyList.Application.Risk;
using BuyList.Domain.Enums;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Application;

public class RiskClassifierTests
{
    private static RiskClassifier Make(RiskOptions? o = null) => new(Options.Create(o ?? new RiskOptions()));

    [Theory]
    [InlineData(0, RiskLevel.VeryHigh, 0.25)]
    [InlineData(9, RiskLevel.VeryHigh, 0.25)]
    [InlineData(10, RiskLevel.High, 0.5)]
    [InlineData(29, RiskLevel.High, 0.5)]
    [InlineData(30, RiskLevel.Medium, 0.8)]
    [InlineData(99, RiskLevel.Medium, 0.8)]
    [InlineData(100, RiskLevel.Low, 1.0)]
    [InlineData(299, RiskLevel.Low, 1.0)]
    [InlineData(300, RiskLevel.VeryLow, 1.0)]
    [InlineData(50_000, RiskLevel.VeryLow, 1.0)]
    public void Default_bands_and_multipliers(int sales, RiskLevel level, double multiplier)
    {
        var r = Make().Classify(sales);
        Assert.Equal(level, r.Level);
        Assert.Equal((decimal)multiplier, r.Multiplier);
        Assert.False(r.NoSalesData);
    }

    [Fact]
    public void Missing_sales_data_is_very_high_risk_and_flagged()
    {
        var r = Make().Classify(null);
        Assert.Equal(RiskLevel.VeryHigh, r.Level);
        Assert.Equal(0.25m, r.Multiplier);
        Assert.True(r.NoSalesData);
    }

    [Fact]
    public void Zero_sales_is_real_data_not_missing_data() => Assert.False(Make().Classify(0).NoSalesData);

    [Fact]
    public void Negative_sales_is_treated_as_missing() => Assert.True(Make().Classify(-5).NoSalesData);

    [Fact]
    public void Configured_bands_override_the_defaults()
    {
        var o = new RiskOptions();
        o.Bands.Add(new RiskBand { MinMonthlySales = 0, Level = RiskLevel.VeryHigh, Multiplier = 0.1m });
        o.Bands.Add(new RiskBand { MinMonthlySales = 50, Level = RiskLevel.Low, Multiplier = 0.9m });

        Assert.Equal(RiskLevel.VeryHigh, Make(o).Classify(49).Level);
        Assert.Equal(0.9m, Make(o).Classify(50).Multiplier);
        Assert.Equal(0.1m, Make(o).Classify(null).Multiplier); // missing data uses the configured VeryHigh multiplier
    }

    [Fact]
    public void Bands_need_not_be_configured_in_order()
    {
        var o = new RiskOptions();
        o.Bands.Add(new RiskBand { MinMonthlySales = 100, Level = RiskLevel.Low, Multiplier = 1m });
        o.Bands.Add(new RiskBand { MinMonthlySales = 0, Level = RiskLevel.VeryHigh, Multiplier = 0.25m });
        Assert.Equal(RiskLevel.Low, Make(o).Classify(150).Level);
        Assert.Equal(RiskLevel.VeryHigh, Make(o).Classify(5).Level);
    }

    [Theory]
    [InlineData(RiskLevel.Low, RiskLevel.Medium, true)]
    [InlineData(RiskLevel.Medium, RiskLevel.Medium, true)]
    [InlineData(RiskLevel.High, RiskLevel.Medium, false)]
    [InlineData(RiskLevel.VeryHigh, RiskLevel.VeryHigh, true)]
    [InlineData(RiskLevel.VeryHigh, RiskLevel.High, false)]
    [InlineData(RiskLevel.VeryLow, RiskLevel.VeryLow, true)]
    [InlineData(RiskLevel.Low, RiskLevel.VeryLow, false)]
    public void Max_risk_filter_hides_anything_riskier(RiskLevel level, RiskLevel max, bool kept) =>
        Assert.Equal(kept, RiskClassifier.IsWithin(level, max));
}
