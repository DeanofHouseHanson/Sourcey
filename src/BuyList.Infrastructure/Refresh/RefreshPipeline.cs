using BuyList.Application.Enrichment;
using BuyList.Application.Import;
using BuyList.Application.Opportunities;

namespace BuyList.Infrastructure.Refresh;

public class RefreshPipeline : IRefreshPipeline
{
    // One refresh at a time per process: two overlapping runs would double-spend Keepa tokens.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IProductMatcher _matcher;
    private readonly IEnrichmentService _enrichment;
    private readonly IOpportunityService _opportunities;

    public RefreshPipeline(IProductMatcher matcher, IEnrichmentService enrichment, IOpportunityService opportunities)
    {
        _matcher = matcher;
        _enrichment = enrichment;
        _opportunities = opportunities;
    }

    public async Task<RefreshResult> RunAsync(CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct))
            throw new InvalidOperationException("A refresh is already running.");
        try
        {
            var match = await _matcher.MatchPendingAsync(ct);
            var enrich = await _enrichment.EnrichAsync(ct);
            var opps = await _opportunities.RecalculateAsync(ct);
            return new RefreshResult(match, enrich, opps);
        }
        finally { Gate.Release(); }
    }
}
