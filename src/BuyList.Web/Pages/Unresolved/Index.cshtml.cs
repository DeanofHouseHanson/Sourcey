using BuyList.Application.Enrichment;
using BuyList.Domain.Entities;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Web.Pages.Unresolved;

public class IndexModel : PageModel
{
    public const int OptionsShownUpFront = 5;

    private readonly BuyListDbContext _db;
    private readonly IAsinResolutionService _resolver;

    public IndexModel(BuyListDbContext db, IAsinResolutionService resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    public IReadOnlyList<Product> Ambiguous { get; private set; } = Array.Empty<Product>();
    public IReadOnlyList<Product> NotFound { get; private set; } = Array.Empty<Product>();
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        Ambiguous = await _db.Products.AsNoTracking()
            .Include(p => p.AsinCandidates).Include(p => p.SourceOffers)
            .Where(p => p.AsinLookupStatus == AsinLookupStatus.Ambiguous).OrderBy(p => p.Id).ToListAsync();
        NotFound = await _db.Products.AsNoTracking().Include(p => p.SourceOffers)
            .Where(p => p.AsinLookupStatus == AsinLookupStatus.NotFound).OrderBy(p => p.Id).ToListAsync();
    }

    public async Task<IActionResult> OnPostChooseAsync(int productId, string asin, CancellationToken ct)
    {
        Message = await _resolver.ChooseAsync(productId, asin, ct) switch
        {
            ChooseAsinOutcome.Chosen => $"Using {asin}. Its market data was saved and the Buy List recalculated.",
            ChooseAsinOutcome.NotACandidate => "That ASIN isn't one of the options for this product (it may already have been settled).",
            _ => "That product no longer exists."
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDismissAsync(int productId, CancellationToken ct)
    {
        Message = await _resolver.DismissAsync(productId, ct)
            ? "Marked as not found on Amazon. Keepa will be asked again after the retry period."
            : "Nothing to dismiss for that product.";
        return RedirectToPage();
    }

    public static IEnumerable<AsinCandidate> Ordered(Product p) =>
        p.AsinCandidates.OrderBy(c => c.SalesRank ?? int.MaxValue).ThenBy(c => c.Asin, StringComparer.Ordinal);
}
