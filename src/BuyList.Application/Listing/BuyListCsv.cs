using System.Globalization;
using System.Text;
using BuyList.Application.Formatting;
using BuyList.Application.Import;

namespace BuyList.Application.Listing;

/// <summary>
/// CSV export of the filtered Buy List. Free text is neutralised against spreadsheet formula injection; numbers are not
/// (a leading "-" on a negative number must stay a number). Missing values are written as "no data".
/// </summary>
public static class BuyListCsv
{
    private static readonly string[] Header =
    {
        "Title", "Brand", "ASIN", "EAN", "Retailer", "Source URL", "Buy ex VAT", "Buy inc VAT", "Price checked (UTC)", "Stale",
        "Avg 90d Buy Box", "Net profit", "ROI %", "Margin %", "Est monthly sales", "Est monthly profit", "Risk",
        "Risk-adjusted monthly profit", "Days to sell", "FBA sellers", "Amazon on listing", "Flags", "Max buy price",
        "Suggested qty", "Status", "Amazon UK", "Keepa"
    };

    public static string Build(IEnumerable<BuyListRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Quote)));
        foreach (var r in rows)
        {
            var cells = new[]
            {
                Text(r.Title), Text(r.Brand), Text(r.Asin), Text(r.Ean), Text(r.Retailer), Text(r.SourceUrl),
                Num(r.BuyPriceExVat), Num(r.BuyPriceIncVat), r.CheckedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                r.IsStale ? "yes" : "no", Num(r.Avg90BuyBox), Num(r.NetProfit), Num(r.RoiPercent), Num(r.MarginPercent),
                r.MonthlySales?.ToString(CultureInfo.InvariantCulture) ?? Gbp.NoData, Num(r.EstMonthlyProfit), r.Risk.ToString(),
                Num(r.RiskAdjustedMonthlyProfit), Num(r.DaysToSell), r.FbaSellerCount.ToString(CultureInfo.InvariantCulture) + (r.FbaSellerCountIsApprox ? " (approx)" : ""),
                r.AmazonOnListing ? "yes" : "no", r.Flags.ToString(), Num(r.MaxBuyPrice), r.SuggestedQty.ToString(CultureInfo.InvariantCulture),
                r.Status.ToString(), Text(AmazonLinks.AmazonUk(r.Asin)), Text(AmazonLinks.Keepa(r.Asin))
            };
            sb.AppendLine(string.Join(",", cells.Select(Quote)));
        }
        return sb.ToString();
    }

    private static string Text(string? v) => CsvSafe.Cell(v);
    private static string Num(decimal? v) => v is null ? Gbp.NoData : v.Value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Quote(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
}
