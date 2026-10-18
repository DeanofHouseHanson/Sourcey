namespace BuyList.Application.Listing;

/// <summary>Quick links for an ASIN. ASINs are validated before being put in a URL.</summary>
public static class AmazonLinks
{
    public static bool IsAsin(string? asin) =>
        asin is { Length: 10 } && asin.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'));

    public static string? AmazonUk(string? asin) => IsAsin(asin) ? $"https://www.amazon.co.uk/dp/{asin}" : null;

    /// <summary>Keepa's product page for amazon.co.uk (domain 2).</summary>
    public static string? Keepa(string? asin) => IsAsin(asin) ? $"https://keepa.com/#!product/2-{asin}" : null;

    public static string? SellerAmp(string? asin) =>
        IsAsin(asin) ? $"https://sas.selleramp.com/sas/lookup?SasLookup%5Bsearch_term%5D={asin}" : null;
}
