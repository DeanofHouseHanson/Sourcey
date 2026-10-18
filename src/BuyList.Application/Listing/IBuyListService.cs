using BuyList.Domain.Enums;

namespace BuyList.Application.Listing;

public interface IBuyListService
{
    Task<BuyListResult> QueryAsync(BuyListQuery query, CancellationToken ct = default);

    /// <summary>Same filters and order, without paging, for CSV export (capped).</summary>
    Task<IReadOnlyList<BuyListRow>> QueryAllAsync(BuyListQuery query, int max = 10_000, CancellationToken ct = default);

    Task<bool> SetStatusAsync(int opportunityId, OpportunityStatus status, CancellationToken ct = default);

    Task<PipelineStatus> GetPipelineStatusAsync(CancellationToken ct = default);
}
