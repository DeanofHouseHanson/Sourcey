namespace BuyList.Application.Opportunities;

public sealed record RecalcSummary(int Created, int Updated, int Removed, int SkippedNoPrice, int SkippedOutOfStock);

public interface IOpportunityService
{
    /// <summary>
    /// Rebuilds opportunities from matched offers + latest Amazon snapshots. Preserves each opportunity's Status.
    /// Offers that no longer qualify lose their opportunity unless the owner has already acted on it (not New).
    /// </summary>
    Task<RecalcSummary> RecalculateAsync(CancellationToken ct = default);
}
