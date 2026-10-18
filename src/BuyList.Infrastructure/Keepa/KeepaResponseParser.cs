using System.Text.Json;
using BuyList.Application.Import;
using BuyList.Application.Keepa;

namespace BuyList.Infrastructure.Keepa;

/// <summary>
/// Turns Keepa's JSON into <see cref="KeepaProduct"/>. Keepa quirks are confined to this class:
/// prices are integer pence, -1 / -2 mean "no data", and per-type arrays are indexed by the csv type number.
/// </summary>
public static class KeepaResponseParser
{
    // Keepa csv type indices used by the stats arrays.
    private const int Amazon = 0;
    private const int Sales = 3;            // sales rank
    private const int CountNew = 11;        // total "new" offers (FBA + FBM)
    private const int BuyBoxShipping = 18;  // Buy Box price incl. shipping (needs buybox=1)
    private const int CountNewFba = 34;

    public static KeepaTokenStatus ParseStatus(JsonElement root) => new(
        Int(root, "tokensLeft"), Int(root, "refillRate"), Int(root, "refillIn"));

    public static int ParseTokensConsumed(JsonElement root) => Int(root, "tokensConsumed") ?? 0;

    public static IReadOnlyList<KeepaProduct> ParseProducts(JsonElement root, DateTime fetchedAtUtc)
    {
        var result = new List<KeepaProduct>();
        if (!root.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var p in products.EnumerateArray())
        {
            var asin = Str(p, "asin");
            if (asin is null) continue;

            var stats = p.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;
            int? fbaCount = null;
            var fbaApprox = false;
            if (stats is { } st)
            {
                var fba = At(st, "current", CountNewFba);
                if (fba is >= 0) fbaCount = fba;
                else if (At(st, "current", CountNew) is >= 0 and var total) { fbaCount = total; fbaApprox = true; }
            }

            result.Add(new KeepaProduct(
                Asin: asin,
                Title: Str(p, "title"),
                Brand: Str(p, "brand"),
                ImageUrl: Image(p),
                Mpn: Str(p, "partNumber") ?? Str(p, "model"),
                Eans: Codes(p),
                Avg90BuyBox: stats is { } a ? Money(At(a, "avg90", BuyBoxShipping)) : null,
                SalesRankCurrent: stats is { } r1 ? Positive(At(r1, "current", Sales)) : null,
                SalesRankAvg90: stats is { } r2 ? Positive(At(r2, "avg90", Sales)) : null,
                SalesRankDrops30: stats is { } d ? NonNegative(Int(d, "salesRankDrops30")) : null,
                FbaSellerCount: fbaCount,
                FbaSellerCountIsApprox: fbaApprox,
                AmazonOnListing: stats is { } am && At(am, "current", Amazon) is > 0,
                MonthlySold: NonNegative(Int(p, "monthlySold")),
                WeightGrams: Positive(Int(p, "packageWeight")) ?? Positive(Int(p, "itemWeight")),
                FbaPickAndPackFee: p.TryGetProperty("fbaFees", out var fees) && fees.ValueKind == JsonValueKind.Object
                    ? Money(Positive(Int(fees, "pickAndPackFee"))) : null,
                FetchedAt: fetchedAtUtc));
        }
        return result;
    }

    private static IReadOnlyList<string> Codes(JsonElement p)
    {
        var codes = new List<string>();
        foreach (var field in new[] { "eanList", "upcList" })
            if (p.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && Gtin.NormaliseOrNull(item.GetString()) is { } c)
                        codes.Add(c);
        return codes.Distinct().ToList();
    }

    private static string? Image(JsonElement p)
    {
        const string baseUrl = "https://m.media-amazon.com/images/I/";
        var csv = Str(p, "imagesCSV");
        var first = csv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null && p.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            foreach (var img in images.EnumerateArray())
            {
                first = Str(img, "l") ?? Str(img, "m");
                if (first is not null) break;
            }
        // Filenames only; never trust a value that could redirect the browser elsewhere.
        return first is not null && first.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or '%' or '+')
            ? baseUrl + first : null;
    }

    private static decimal? Money(int? pence) => pence is >= 0 ? pence.Value / 100m : null;
    private static int? Positive(int? v) => v is > 0 ? v : null;
    private static int? NonNegative(int? v) => v is >= 0 ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var i) ? i : null;

    private static int? At(JsonElement e, string array, int index)
    {
        if (!e.TryGetProperty(array, out var arr) || arr.ValueKind != JsonValueKind.Array || index >= arr.GetArrayLength()) return null;
        var v = arr[index];
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }
}
