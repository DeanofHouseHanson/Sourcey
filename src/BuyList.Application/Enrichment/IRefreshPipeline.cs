using BuyList.Application.Import;
using BuyList.Application.Opportunities;

namespace BuyList.Application.Enrichment;

public sealed record RefreshResult(MatchSummary Matching, EnrichmentSummary Enrichment, RecalcSummary Opportunities);

/// <summary>The whole refresh: match new offers -> enrich from Keepa within budget -> recalculate opportunities.</summary>
public interface IRefreshPipeline
{
    Task<RefreshResult> RunAsync(CancellationToken ct = default);
}
