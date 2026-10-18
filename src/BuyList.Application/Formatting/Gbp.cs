using System.Globalization;

namespace BuyList.Application.Formatting;

/// <summary>GBP formatting that doesn't depend on the host's ICU/culture data.</summary>
public static class Gbp
{
    /// <summary>What the UI shows wherever a value is missing: plainly "no data", never a guess.</summary>
    public const string NoData = "no data";

    public static string Format(decimal value) =>
        (value < 0 ? "-£" : "£") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

    public static string Format(decimal? value) => value is null ? NoData : Format(value.Value);
}
