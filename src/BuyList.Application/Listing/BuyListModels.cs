using BuyList.Domain.Enums;

namespace BuyList.Application.Listing;

/// <summary>One flattened Buy List line. Nullable members are genuinely missing data (shown as "no data").</summary>
public sealed record BuyListRow(
    int OpportunityId,
    string? Asin,
    string? Ean,
    string? Title,
    string? Brand,
    string? ImageUrl,
    string Retailer,
    string SourceUrl,
    decimal BuyPriceExVat,
    decimal BuyPriceIncVat,
    DateTime CheckedAt,
    bool IsStale,
    decimal? Avg90BuyBox,
    decimal NetProfit,
    decimal RoiPercent,
    decimal MarginPercent,
    int? MonthlySales,
    decimal EstMonthlyProfit,
    RiskLevel Risk,
    decimal RiskAdjustedMonthlyProfit,
    decimal? DaysToSell,
    int FbaSellerCount,
    bool FbaSellerCountIsApprox,
    bool AmazonOnListing,
    OpportunityFlags Flags,
    decimal MaxBuyPrice,
    int SuggestedQty,
    OpportunityStatus Status);

/// <summary>How many otherwise-eligible rows each active default filter is currently hiding (so an empty list can be explained).</summary>
public sealed record HiddenSummary(int LossMaking, int AmazonOnListing, int Stale, int Ignored, int Blocklisted)
{
    public int Total => LossMaking + AmazonOnListing + Stale + Ignored + Blocklisted;
}

public sealed record BuyListResult(
    IReadOnlyList<BuyListRow> Rows,
    int TotalRows,
    int Page,
    int PageSize,
    IReadOnlyList<string> Retailers,
    HiddenSummary Hidden,
    DateTime? LastCalculatedAt)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalRows / (double)PageSize));
}

/// <summary>Where the pipeline stands, used to explain an empty Buy List instead of showing a blank page.</summary>
public sealed record PipelineStatus(
    int Offers,
    int OffersMatched,
    int ProductsAwaitingLookup,
    int ProductsAmbiguous,
    int ProductsNotFound,
    int ProductsWithoutBuyBoxPrice,
    int Opportunities);
