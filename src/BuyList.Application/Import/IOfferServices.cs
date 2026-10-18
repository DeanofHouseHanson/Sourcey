namespace BuyList.Application.Import;

public interface IOfferImportService
{
    /// <summary>Upserts offers by SourceUrl, then matches every unmatched offer to a product.</summary>
    Task<ImportSummary> ImportAsync(IReadOnlyList<ValidOffer> offers, int rejectedCount, CancellationToken ct = default);
}

public interface IProductMatcher
{
    /// <summary>
    /// Matches every offer that has no product yet: by EAN/GTIN first, else exact brand + MPN. Never by title.
    /// Safe to call repeatedly (the refresh job reuses it).
    /// </summary>
    Task<MatchSummary> MatchPendingAsync(CancellationToken ct = default);
}
