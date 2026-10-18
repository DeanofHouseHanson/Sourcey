namespace BuyList.Application.Enrichment;

public sealed record EnrichmentSummary(
    int CodesLookedUp,
    int AsinsResolved,
    int CodesNotFound,
    int CodesAmbiguous,
    int ProductsRefreshed,
    int SnapshotsWritten,
    int TokensUsed,
    int? TokensLeft,
    int ProductsDeferred,
    bool Throttled);

public interface IEnrichmentService
{
    /// <summary>
    /// 1) Resolve ASINs for stub products (EAN lookups), 2) refresh market data older than Keepa:RefreshAgeHours.
    /// Spends at most <c>Keepa:MaxTokensPerRun</c> tokens and never dips into the reserve; leftovers are deferred to the next run.
    /// </summary>
    Task<EnrichmentSummary> EnrichAsync(CancellationToken ct = default);
}
