using BuyList.Application.Options;
using BuyList.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace BuyList.Tests.Application;

/// <summary>Guards the shipped appsettings.json against drifting from the agreed defaults.</summary>
public class ConfigDefaultsTests
{
    private static IConfiguration Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "BuyList.Web", "appsettings.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return new ConfigurationBuilder().AddJsonFile(Path.Combine(dir!.FullName, "src", "BuyList.Web", "appsettings.json")).Build();
    }

    [Fact]
    public void Risk_bands_in_appsettings_match_the_specification()
    {
        var bands = Load().GetSection("Risk").Get<RiskOptions>()!.Bands;
        Assert.Equal(
            RiskOptions.DefaultBands.Select(b => (b.MinMonthlySales, b.Level, b.Multiplier)),
            bands.Select(b => (b.MinMonthlySales, b.Level, b.Multiplier)));
    }

    [Fact]
    public void Profit_defaults_are_sensible()
    {
        var p = Load().GetSection("Profit").Get<ProfitOptions>()!;
        Assert.True(p.VatRegistered);
        Assert.Equal(0.20m, p.VatRate);
    }

    [Fact]
    public void Fba_tiers_are_configured()
    {
        var fba = Load().GetSection("Fba").Get<FbaFeeOptions>()!;
        Assert.NotEmpty(fba.Tiers);
        Assert.All(fba.Tiers, t => Assert.True(t.Fee > 0));
    }

    [Fact]
    public void Appsettings_contains_no_keepa_api_key() =>
        Assert.Null(Load()["Keepa:ApiKey"]);
}
