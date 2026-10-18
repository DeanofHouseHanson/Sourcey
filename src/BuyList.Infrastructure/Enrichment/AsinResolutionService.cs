using BuyList.Application.Enrichment;
using BuyList.Application.Opportunities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Infrastructure.Enrichment;

public class AsinResolutionService : IAsinResolutionService
{
    private readonly BuyListDbContext _db;
    private readonly IOpportunityService _opportunities;

    public AsinResolutionService(BuyListDbContext db, IOpportunityService opportunities)
    {
        _db = db;
        _opportunities = opportunities;
    }

    public async Task<ChooseAsinOutcome> ChooseAsync(int productId, string asin, CancellationToken ct = default)
    {
        var product = await _db.Products.Include(p => p.AsinCandidates).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return ChooseAsinOutcome.ProductNotFound;

        // Only an option Keepa actually offered for this product can be chosen; never a free-typed ASIN.
        var candidate = product.AsinCandidates.FirstOrDefault(c => c.Asin == asin);
        if (product.AsinLookupStatus != AsinLookupStatus.Ambiguous || candidate is null) return ChooseAsinOutcome.NotACandidate;

        var k = ProductAssignment.ToKeepaProduct(candidate, product.Ean);
        _db.AsinCandidates.RemoveRange(product.AsinCandidates);
        var target = await ProductAssignment.ResolveAsync(_db, product, k, ct);
        ProductAssignment.AddSnapshot(_db, target, k);
        await _db.SaveChangesAsync(ct);

        await _opportunities.RecalculateAsync(ct);
        return ChooseAsinOutcome.Chosen;
    }

    public async Task<bool> DismissAsync(int productId, CancellationToken ct = default)
    {
        var product = await _db.Products.Include(p => p.AsinCandidates).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null || product.AsinLookupStatus != AsinLookupStatus.Ambiguous) return false;
        _db.AsinCandidates.RemoveRange(product.AsinCandidates);
        product.AsinLookupStatus = AsinLookupStatus.NotFound;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
