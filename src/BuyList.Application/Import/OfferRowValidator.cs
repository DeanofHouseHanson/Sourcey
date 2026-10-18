using System.Globalization;
using System.Text.RegularExpressions;
using BuyList.Application.Options;
using BuyList.Domain.Enums;
using Microsoft.Extensions.Options;

namespace BuyList.Application.Import;

/// <summary>Validates one CSV row, collecting EVERY problem so the user can fix a row in one pass.</summary>
public class OfferRowValidator
{
    private static readonly Regex PriceShape = new(@"^\d{1,3}(,\d{3})+(\.\d+)?$|^\d+(\.\d+)?$", RegexOptions.Compiled);
    private readonly decimal _vatRate;

    public OfferRowValidator(IOptions<ProfitOptions> profit) => _vatRate = profit.Value.VatRate;

    public (ValidOffer? Offer, IReadOnlyList<string> Errors) Validate(RawOfferRow row)
    {
        var errors = new List<string>();

        var retailer = row.Get("retailer");
        if (retailer.Length == 0) errors.Add("retailer is required");
        else if (retailer.Length > 200) errors.Add("retailer is longer than 200 characters");

        var url = ValidateUrl(row.Get("url"), errors);

        string? ean = null;
        var rawEan = row.Get("ean");
        if (rawEan.Length > 0)
        {
            if (Gtin.TryNormalise(rawEan, out var canonical, out var eanError)) ean = canonical;
            else errors.Add(eanError!);
        }

        var mpn = Limit(row.Get("mpn"), "mpn", 100, errors);
        var brand = Limit(row.Get("brand"), "brand", 100, errors);
        var title = Limit(row.Get("title"), "title", 500, errors);

        var price = ParsePrice(row.Get("price"), errors);
        var vatIncluded = ParseVatFlag(row.Get("vat_included"), errors);

        if (errors.Count > 0 || price is null || vatIncluded is null)
            return (null, errors);

        var (ex, inc) = vatIncluded.Value
            ? (Money(price.Value / (1m + _vatRate)), price.Value)
            : (price.Value, Money(price.Value * (1m + _vatRate)));

        var matchable = ean is not null || (brand is not null && mpn is not null);
        return (new ValidOffer(row.RowNumber, retailer, url!, ean, rawEan.Length > 0 ? rawEan : null,
            mpn, brand, title, ex, inc, ParseStock(row.Get("stock")), matchable), errors);
    }

    /// <summary>Absolute https URL with a host and no embedded credentials; returned in normalised form.</summary>
    public static string? ValidateUrl(string raw, List<string> errors)
    {
        if (raw.Length == 0) { errors.Add("url is required (every offer needs a source link)"); return null; }
        if (raw.Length > 2048) { errors.Add("url is longer than 2048 characters"); return null; }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            errors.Add($"url '{raw}' is not an absolute URL");
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"url must start with https:// (got '{uri.Scheme}://')");
            return null;
        }
        if (string.IsNullOrEmpty(uri.Host)) { errors.Add("url has no host"); return null; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { errors.Add("url must not contain a username or password"); return null; }

        // Lower-cased scheme/host and no fragment, so the same page can't be imported twice under different spellings.
        return new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private static decimal? ParsePrice(string raw, List<string> errors)
    {
        var s = raw.Replace("£", "").Replace(" ", "");
        if (s.Length == 0) { errors.Add("price is required"); return null; }
        // Commas are accepted only as thousands separators ("1,234.50"); "12,50" is rejected rather than read as 1250.
        if (!PriceShape.IsMatch(s)) { errors.Add($"price '{raw}' is not a valid amount (use e.g. 12.50)"); return null; }
        if (!decimal.TryParse(s.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price))
        {
            errors.Add($"price '{raw}' is not a valid amount");
            return null;
        }
        if (price <= 0) { errors.Add("price must be greater than zero"); return null; }
        if (price > 100_000) { errors.Add("price is implausibly large (over £100,000)"); return null; }
        return price;
    }

    private static bool? ParseVatFlag(string raw, List<string> errors)
    {
        switch (raw.ToLowerInvariant())
        {
            case "true" or "yes" or "y" or "1": return true;
            case "false" or "no" or "n" or "0": return false;
            case "":
                errors.Add("vat_included is required (true/false): the price can't be interpreted without it");
                return null;
            default:
                errors.Add($"vat_included '{raw}' must be true/false, yes/no or 1/0");
                return null;
        }
    }

    private static StockStatus ParseStock(string raw) => raw.ToLowerInvariant().Replace("_", " ").Replace("-", " ") switch
    {
        "in stock" or "instock" or "available" or "yes" or "y" => StockStatus.InStock,
        "low stock" or "low" or "limited" => StockStatus.LowStock,
        "out of stock" or "oos" or "unavailable" or "no" or "n" => StockStatus.OutOfStock,
        _ => StockStatus.Unknown
    };

    private static string? Limit(string value, string column, int max, List<string> errors)
    {
        if (value.Length == 0) return null;
        if (value.Length > max) { errors.Add($"{column} is longer than {max} characters"); return null; }
        return value;
    }

    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
