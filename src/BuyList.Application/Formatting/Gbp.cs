using System.Globalization;

namespace BuyList.Application.Formatting;

/// <summary>GBP formatting that doesn't depend on the host's ICU/culture data.</summary>
public static class Gbp
{
    public static string Format(decimal value) =>
        (value < 0 ? "-£" : "£") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

    public static string Format(decimal? value) => value is null ? "–" : Format(value.Value);
}
