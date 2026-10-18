using System.Linq.Expressions;
using BuyList.Application.Listing;
using BuyList.Application.Options;
using BuyList.Application.Ranking;
using BuyList.Application.Risk;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuyList.Infrastructure.Listing;

public class BuyListService : IBuyListService
{
    // Hide-flagged covers eligibility problems only; "no sales data" is handled by the min-monthly-sales filter.
    private const OpportunityFlags EligibilityFlags = OpportunityFlags.Gated | OpportunityFlags.Hazmat | OpportunityFlags.IpComplaintProne;

    private readonly BuyListDbContext _db;
    private readonly RankingOptions _ranking;
    private readonly TimeProvider _time;

    public BuyListService(BuyListDbContext db, IOptions<RankingOptions> ranking, TimeProvider time)
    {
        _db = db;
        _ranking = ranking.Value;
        _time = time;
    }

    /// <summary>An opportunity with the product's latest Amazon snapshot.</summary>
    private sealed class Row
    {
        public Opportunity O { get; set; } = null!;
        public AmazonSnapshot S { get; set; } = null!;
    }

    private IQueryable<Row> Base() =>
        from o in _db.Opportunities
        join s in _db.AmazonSnapshots.Where(s => !_db.AmazonSnapshots.Any(n =>
            n.ProductId == s.ProductId && (n.CapturedAt > s.CapturedAt || (n.CapturedAt == s.CapturedAt && n.Id > s.Id))))
            on o.ProductId equals s.ProductId
        select new Row { O = o, S = s };

    private DateTime StaleCutoff() => _time.GetUtcNow().UtcDateTime.AddHours(-_ranking.StaleAfterHours);

    public async Task<BuyListResult> QueryAsync(BuyListQuery query, CancellationToken ct = default)
    {
        var cutoff = StaleCutoff();
        var filtered = Order(Filter(Base(), query, cutoff), query);
        var total = await filtered.CountAsync(ct);

        var page = Math.Min(query.SafePage, Math.Max(1, (int)Math.Ceiling(total / (double)query.SafePageSize)));
        var rows = await filtered.Skip((page - 1) * query.SafePageSize).Take(query.SafePageSize)
            .Select(Project(cutoff)).ToListAsync(ct);

        var retailers = await _db.Opportunities.Select(o => o.SourceOffer!.RetailerName).Distinct().OrderBy(r => r).ToListAsync(ct);
        var last = await _db.Opportunities.MaxAsync(o => (DateTime?)o.CalculatedAt, ct);

        return new BuyListResult(rows, total, page, query.SafePageSize, retailers,
            query.View == BuyListView.List ? await HiddenAsync(query, cutoff, ct) : new HiddenSummary(0, 0, 0, 0, 0), last);
    }

    public async Task<IReadOnlyList<BuyListRow>> QueryAllAsync(BuyListQuery query, int max = 10_000, CancellationToken ct = default) =>
        await Order(Filter(Base(), query, StaleCutoff()), query).Take(max).Select(Project(StaleCutoff())).ToListAsync(ct);

    public async Task<bool> SetStatusAsync(int opportunityId, OpportunityStatus status, CancellationToken ct = default)
    {
        var opp = await _db.Opportunities.FindAsync(new object[] { opportunityId }, ct);
        if (opp is null) return false;
        opp.Status = status;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PipelineStatus> GetPipelineStatusAsync(CancellationToken ct = default) => new(
        Offers: await _db.SourceOffers.CountAsync(ct),
        OffersMatched: await _db.SourceOffers.CountAsync(o => o.ProductId != null, ct),
        ProductsAwaitingLookup: await _db.Products.CountAsync(p => p.Asin == null && p.Ean != null && p.AsinLookupStatus == AsinLookupStatus.NotTried, ct),
        ProductsAmbiguous: await _db.Products.CountAsync(p => p.AsinLookupStatus == AsinLookupStatus.Ambiguous, ct),
        ProductsNotFound: await _db.Products.CountAsync(p => p.AsinLookupStatus == AsinLookupStatus.NotFound, ct),
        ProductsWithoutBuyBoxPrice: await _db.Products.CountAsync(p => p.Asin != null && p.Snapshots.Any() && !p.Snapshots.Any(s => s.Avg90BuyBox != null), ct),
        Opportunities: await _db.Opportunities.CountAsync(ct));

    // ---------------- filtering ----------------

    private static IQueryable<Row> Filter(IQueryable<Row> q, BuyListQuery f, DateTime staleCutoff)
    {
        if (f.View == BuyListView.Bought)
            return q.Where(r => r.O.Status == OpportunityStatus.Bought);   // your purchases: no default exclusions

        q = f.Status is { } s
            ? q.Where(r => r.O.Status == s)
            : q.Where(r => r.O.Status == OpportunityStatus.New || r.O.Status == OpportunityStatus.Checked);   // Ignored stays hidden

        q = q.Where(r => (r.O.Flags & OpportunityFlags.Blocklisted) == 0);
        q = ApplyOtherFilters(q, f);

        if (f.MinNetProfit is { } profit) q = q.Where(r => r.O.NetProfit >= profit);
        if (f.HideAmazon) q = q.Where(r => !r.S.AmazonOnListing);
        if (!f.IncludeStale) q = q.Where(r => r.O.SourceOffer!.CheckedAt >= staleCutoff);
        return q;
    }

    /// <summary>The filters the user chooses to narrow the list (as opposed to the default exclusions: profit, Amazon, stale, status).</summary>
    private static IQueryable<Row> ApplyOtherFilters(IQueryable<Row> q, BuyListQuery f)
    {
        if (f.MinRoi is { } roi) q = q.Where(r => r.O.RoiPercent >= roi);
        if (f.MinMonthlySales is { } sales) q = q.Where(r => r.S.MonthlySales >= sales);
        if (f.MaxUnitCost is { } cost) q = q.Where(r => r.O.SourceOffer!.BuyPriceIncVat <= cost);
        if (!string.IsNullOrWhiteSpace(f.Retailer)) { var name = f.Retailer; q = q.Where(r => r.O.SourceOffer!.RetailerName == name); }
        if (f.HideFlagged) q = q.Where(r => (r.O.Flags & EligibilityFlags) == 0);
        if (f.MaxRisk is { } max)
        {
            var allowed = Enum.GetValues<RiskLevel>().Where(l => RiskClassifier.IsWithin(l, max)).ToList();
            q = q.Where(r => allowed.Contains(r.O.RiskLevel));
        }
        return q;
    }

    private async Task<HiddenSummary> HiddenAsync(BuyListQuery f, DateTime cutoff, CancellationToken ct)
    {
        // Rows that are New/Checked and not blocklisted, so each count shows what one default filter alone is hiding.
        // Scoped to the user's other filters too (retailer, ROI...), so the counts describe what THIS view is hiding.
        var scoped = ApplyOtherFilters(Base(), f);
        var pool = scoped.Where(r => (r.O.Status == OpportunityStatus.New || r.O.Status == OpportunityStatus.Checked)
                                      && (r.O.Flags & OpportunityFlags.Blocklisted) == 0);
        return new HiddenSummary(
            LossMaking: f.MinNetProfit is { } p && p >= 0 ? await pool.CountAsync(r => r.O.NetProfit < p, ct) : 0,
            AmazonOnListing: f.HideAmazon ? await pool.CountAsync(r => r.S.AmazonOnListing, ct) : 0,
            Stale: f.IncludeStale ? 0 : await pool.CountAsync(r => r.O.SourceOffer!.CheckedAt < cutoff, ct),
            Ignored: f.Status is null ? await scoped.CountAsync(r => r.O.Status == OpportunityStatus.Ignored, ct) : 0,
            Blocklisted: await scoped.CountAsync(r => (r.O.Flags & OpportunityFlags.Blocklisted) != 0, ct));
    }

    // ---------------- ordering (whitelisted keys only) ----------------

    private static IOrderedQueryable<Row> Order(IQueryable<Row> q, BuyListQuery f)
    {
        var sort = f.Sort;
        var desc = f.Desc;
        if (sort == BuyListSort.Default)
        {
            sort = f.Ranking == RankingMode.Raw ? BuyListSort.Monthly : BuyListSort.Adjusted;
            desc = true;
        }

        var ordered = sort switch
        {
            BuyListSort.Title => By(q, r => r.O.Product!.Title, desc),
            BuyListSort.Buy => By(q, r => r.O.SourceOffer!.BuyPriceIncVat, desc),
            BuyListSort.Retailer => By(q, r => r.O.SourceOffer!.RetailerName, desc),
            BuyListSort.Checked => By(q, r => r.O.SourceOffer!.CheckedAt, desc),
            BuyListSort.Avg => By(q, r => r.S.Avg90BuyBox, desc),
            BuyListSort.Profit => By(q, r => r.O.NetProfit, desc),
            BuyListSort.Roi => By(q, r => r.O.RoiPercent, desc),
            BuyListSort.Sales => By(q, r => r.S.MonthlySales, desc),
            BuyListSort.Monthly => By(q, r => r.O.EstMonthlyProfit, desc),
            BuyListSort.Risk => By(q, r => r.O.RiskLevel == RiskLevel.VeryHigh ? 0 : r.O.RiskLevel == RiskLevel.High ? 1
                : r.O.RiskLevel == RiskLevel.Medium ? 2 : r.O.RiskLevel == RiskLevel.Low ? 3 : 4, desc),
            BuyListSort.Days => By(q, r => r.O.DaysToSell, desc),
            BuyListSort.Fba => By(q, r => r.S.FbaSellerCount, desc),
            BuyListSort.MaxBuy => By(q, r => r.O.MaxBuyPrice, desc),
            BuyListSort.Qty => By(q, r => r.O.SuggestedQty, desc),
            BuyListSort.Status => By(q, r => r.O.Status == OpportunityStatus.New ? 0 : r.O.Status == OpportunityStatus.Checked ? 1
                : r.O.Status == OpportunityStatus.Bought ? 2 : 3, desc),
            _ => By(q, r => r.O.RiskAdjustedMonthlyProfit, desc)
        };

        // Same deterministic tie-breaks as OpportunityRanker: better ROI, more profit per unit, then oldest row.
        return ordered.ThenByDescending(r => r.O.RoiPercent).ThenByDescending(r => r.O.NetProfit).ThenBy(r => r.O.Id);
    }

    private static IOrderedQueryable<Row> By<TKey>(IQueryable<Row> q, Expression<Func<Row, TKey>> key, bool desc) =>
        desc ? q.OrderByDescending(key) : q.OrderBy(key);

    // ---------------- projection ----------------

    private static Expression<Func<Row, BuyListRow>> Project(DateTime cutoff) => r => new BuyListRow(
        r.O.Id,
        r.O.Product!.Asin,
        r.O.Product.Ean,
        r.O.Product.Title,
        r.O.Product.Brand,
        r.O.Product.ImageUrl,
        r.O.SourceOffer!.RetailerName,
        r.O.SourceOffer.SourceUrl,
        r.O.SourceOffer.BuyPriceExVat,
        r.O.SourceOffer.BuyPriceIncVat,
        r.O.SourceOffer.CheckedAt,
        r.O.SourceOffer.CheckedAt < cutoff,
        r.S.Avg90BuyBox,
        r.O.NetProfit,
        r.O.RoiPercent,
        r.O.MarginPercent,
        r.S.MonthlySales,
        r.O.EstMonthlyProfit,
        r.O.RiskLevel,
        r.O.RiskAdjustedMonthlyProfit,
        r.O.DaysToSell,
        r.S.FbaSellerCount,
        r.S.FbaSellerCountIsApprox,
        r.S.AmazonOnListing,
        r.O.Flags,
        r.O.MaxBuyPrice,
        r.O.SuggestedQty,
        r.O.Status);
}
