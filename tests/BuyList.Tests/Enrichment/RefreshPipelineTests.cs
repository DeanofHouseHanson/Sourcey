using BuyList.Application.Enrichment;
using BuyList.Application.Import;
using BuyList.Application.Opportunities;
using BuyList.Infrastructure.Refresh;

namespace BuyList.Tests.Enrichment;

public class RefreshPipelineTests
{
    private sealed class Stubs : IProductMatcher, IEnrichmentService, IOpportunityService
    {
        public List<string> Order { get; } = new();
        public Func<Task>? InEnrichment { get; set; }
        public Task<MatchSummary> MatchPendingAsync(CancellationToken ct = default)
        { Order.Add("match"); return Task.FromResult(new MatchSummary(0, 0, 0, 0, 0, 0)); }
        public async Task<EnrichmentSummary> EnrichAsync(CancellationToken ct = default)
        { Order.Add("enrich"); if (InEnrichment is not null) await InEnrichment(); return new EnrichmentSummary(0, 0, 0, 0, 0, 0, 0, null, 0, false); }
        public Task<RecalcSummary> RecalculateAsync(CancellationToken ct = default)
        { Order.Add("recalc"); return Task.FromResult(new RecalcSummary(0, 0, 0, 0, 0)); }
    }

    [Fact]
    public async Task Runs_match_then_enrich_then_recalculate()
    {
        var s = new Stubs();
        await new RefreshPipeline(s, s, s).RunAsync();
        Assert.Equal(new[] { "match", "enrich", "recalc" }, s.Order);
    }

    [Fact]
    public async Task A_second_refresh_while_one_is_running_is_refused_so_tokens_are_not_double_spent()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var first = new Stubs { InEnrichment = () => { entered.SetResult(); return gate.Task; } };
        var running = new RefreshPipeline(first, first, first).RunAsync();
        await entered.Task;

        var second = new Stubs();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RefreshPipeline(second, second, second).RunAsync());
        Assert.Empty(second.Order);

        gate.SetResult();
        await running;
        await new RefreshPipeline(second, second, second).RunAsync();   // the gate is released afterwards
    }

    [Fact]
    public async Task The_gate_is_released_even_if_a_stage_throws()
    {
        var bad = new Stubs { InEnrichment = () => throw new InvalidOperationException("boom") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RefreshPipeline(bad, bad, bad).RunAsync());
        var ok = new Stubs();
        await new RefreshPipeline(ok, ok, ok).RunAsync();
        Assert.Equal(3, ok.Order.Count);
    }
}
