using BuyList.Application.Eligibility;
using BuyList.Application.Opportunities;
using BuyList.Application.Profit;
using BuyList.Application.Ranking;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Infrastructure.Opportunities;

public class OpportunityService : IOpportunityService
{
    private readonly BuyListDbContext _db;
    private readonly ProfitCalculator _profit;
    private readonly OpportunityScorer _scorer;
    private readonly IEligibilityChecker _eligibility;
    private readonly TimeProvider _time;

    public OpportunityService(BuyListDbContext db, ProfitCalculator profit, OpportunityScorer scorer, IEligibilityChecker eligibility, TimeProvider time)
    {
        _db = db;
        _profit = profit;
        _scorer = scorer;
        _eligibility = eligibility;
        _time = time;
    }

    public async Task<RecalcSummary> RecalculateAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        int created = 0, updated = 0, removed = 0, noPrice = 0, outOfStock = 0;

        var offers = await _db.SourceOffers.Include(o => o.Product)
            .Where(o => o.ProductId != null && o.Product!.Asin != null).ToListAsync(ct);

        // Latest snapshot per product (anti-join: no newer snapshot exists).
        var latest = new Dictionary<int, AmazonSnapshot>();
        foreach (var s in await _db.AmazonSnapshots
                     .Where(s => !_db.AmazonSnapshots.Any(n => n.ProductId == s.ProductId && n.CapturedAt > s.CapturedAt))
                     .ToListAsync(ct))
            latest[s.ProductId] = s;

        var existing = await _db.Opportunities.ToDictionaryAsync(o => o.SourceOfferId, ct);
        var keep = new HashSet<int>();

        foreach (var offer in offers)
        {
            if (offer.StockStatus == StockStatus.OutOfStock) { outOfStock++; continue; }
            if (!latest.TryGetValue(offer.ProductId!.Value, out var snap) || snap.Avg90BuyBox is not { } sell || sell <= 0)
            {
                noPrice++;
                continue;
            }

            var p = _profit.Calculate(new ProfitInput(sell, offer.BuyPriceExVat, offer.BuyPriceIncVat, snap.WeightGrams, snap.KeepaFbaFee));
            var s = _scorer.Score(new ScoreInput(p.NetProfit, offer.BuyPriceIncVat, snap.MonthlySales, snap.FbaSellerCount));

            var flags = _eligibility.Check(offer.Product!);
            if (s.NoSalesData) flags |= OpportunityFlags.NoSalesData;

            if (!existing.TryGetValue(offer.Id, out var opp))
            {
                opp = new Opportunity { SourceOfferId = offer.Id, Status = OpportunityStatus.New };
                _db.Opportunities.Add(opp);
                created++;
            }
            else updated++;

            opp.ProductId = offer.ProductId.Value;
            opp.NetProfit = p.NetProfit;
            opp.RoiPercent = p.RoiPercent;
            opp.MarginPercent = p.MarginPercent;
            opp.MaxBuyPrice = p.MaxBuyPriceIncVat;   // the price at the till, comparable with BuyPriceIncVat
            opp.EstMonthlyProfit = s.EstMonthlyProfit;
            opp.RiskLevel = s.RiskLevel;
            opp.RiskAdjustedMonthlyProfit = s.RiskAdjustedMonthlyProfit;
            opp.DaysToSell = s.DaysToSell;
            opp.SuggestedQty = s.SuggestedQty;
            opp.Flags = flags;
            opp.CalculatedAt = now;
            keep.Add(offer.Id);
        }

        // Rows that no longer qualify go away, unless the owner has already acted on them (Checked/Bought/Ignored).
        foreach (var (offerId, opp) in existing)
            if (!keep.Contains(offerId) && opp.Status == OpportunityStatus.New)
            {
                _db.Opportunities.Remove(opp);
                removed++;
            }

        await _db.SaveChangesAsync(ct);
        return new RecalcSummary(created, updated, removed, noPrice, outOfStock);
    }
}
