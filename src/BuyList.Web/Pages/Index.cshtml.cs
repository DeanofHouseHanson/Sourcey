using BuyList.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Web.Pages;

public class IndexModel : PageModel
{
    private readonly BuyListDbContext _db;
    public IndexModel(BuyListDbContext db) => _db = db;

    public int ProductCount { get; private set; }
    public int OfferCount { get; private set; }

    public async Task OnGetAsync()
    {
        ProductCount = await _db.Products.CountAsync();
        OfferCount = await _db.SourceOffers.CountAsync();
    }
}
