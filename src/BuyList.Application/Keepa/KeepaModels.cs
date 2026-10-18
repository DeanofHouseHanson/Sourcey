namespace BuyList.Application.Keepa;

/// <summary>Clean domain view of a Keepa product; nothing Keepa-specific (cents, -1 sentinels, csv indices) leaks past the client.</summary>
/// <param name="Eans">All EAN/UPC codes Keepa lists for the ASIN, in canonical GTIN form.</param>
/// <param name="Avg90BuyBox">90-day average Buy Box price (GBP, inc VAT and shipping); null if Keepa has none.</param>
/// <param name="FbaSellerCount">Number of FBA sellers; null if unknown.</param>
/// <param name="FbaSellerCountIsApprox">True when FbaSellerCount is really the total "new" offer count (an over-estimate).</param>
/// <param name="AmazonOnListing">Amazon itself currently has an in-stock offer on the listing.</param>
/// <param name="MonthlySold">Keepa's "bought in past month" figure; null for the many ASINs that don't have one. Never guessed.</param>
/// <param name="FbaPickAndPackFee">Amazon's FBA fee as Keepa reports it (GBP, ex VAT), if known.</param>
public sealed record KeepaProduct(
    string Asin,
    string? Title,
    string? Brand,
    string? ImageUrl,
    string? Mpn,
    IReadOnlyList<string> Eans,
    decimal? Avg90BuyBox,
    int? SalesRankCurrent,
    int? SalesRankAvg90,
    int? SalesRankDrops30,
    int? FbaSellerCount,
    bool FbaSellerCountIsApprox,
    bool AmazonOnListing,
    int? MonthlySold,
    int? WeightGrams,
    decimal? FbaPickAndPackFee,
    DateTime FetchedAt);

/// <param name="TokensLeft">Null if never observed.</param>
/// <param name="RefillRatePerMinute">Tokens regained per minute.</param>
/// <param name="RefillInMs">Milliseconds until the next refill tick.</param>
public sealed record KeepaTokenStatus(int? TokensLeft, int? RefillRatePerMinute, int? RefillInMs);

/// <param name="Products">Everything Keepa returned (a code may return several ASINs).</param>
/// <param name="NotFetched">Requested items that were deliberately NOT sent because the token budget didn't allow it.</param>
/// <param name="TokensUsed">Tokens Keepa reports having consumed by this call.</param>
/// <param name="Throttled">Keepa answered "not enough tokens" (429); stop and wait for a refill.</param>
public sealed record KeepaFetchResult(
    IReadOnlyList<KeepaProduct> Products,
    IReadOnlyList<string> NotFetched,
    int TokensUsed,
    KeepaTokenStatus Tokens,
    bool Throttled);
