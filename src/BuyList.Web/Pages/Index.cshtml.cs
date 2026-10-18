using System.Text;
using BuyList.Application.Listing;
using BuyList.Application.Enrichment;
using BuyList.Application.Options;
using BuyList.Domain.Enums;
using BuyList.Infrastructure.Keepa;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BuyList.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IBuyListService _list;
    private readonly IRefreshPipeline _refresh;
    private readonly KeepaOptions _keepa;
    private readonly TimeProvider _time;

    public IndexModel(IBuyListService list, IRefreshPipeline refresh, IOptions<KeepaOptions> keepa, TimeProvider time)
    {
        _list = list;
        _refresh = refresh;
        _keepa = keepa.Value;
        _time = time;
    }

    public BuyListQuery Q { get; private set; } = new();
    public BuyListResult Result { get; private set; } = null!;
    public PipelineStatus Pipeline { get; private set; } = null!;
    public DateTime Now => _time.GetUtcNow().UtcDateTime;
    public bool LiveKeepa => !_keepa.UseFake;
    public int TokenCap => _keepa.MaxTokensPerRun;
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync([FromQuery] BuyListQuery q, CancellationToken ct)
    {
        Q = q;
        Result = await _list.QueryAsync(Q, ct);
        Pipeline = await _list.GetPipelineStatusAsync(ct);
    }

    public async Task<IActionResult> OnGetExportAsync([FromQuery] BuyListQuery q, CancellationToken ct)
    {
        var rows = await _list.QueryAllAsync(q, ct: ct);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuyListCsv.Build(rows))).ToArray();   // BOM so Excel reads UTF-8
        return File(bytes, "text/csv", $"buy-list-{Now:yyyyMMdd-HHmm}.csv");
    }

    public async Task<IActionResult> OnPostStatusAsync(int id, OpportunityStatus status, string? returnUrl, CancellationToken ct)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(status)) return BadRequest();   // an unparseable value must not silently become "New"
        await _list.SetStatusAsync(id, status, ct);
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostRefreshAsync(string? returnUrl, CancellationToken ct)
    {
        try
        {
            var r = await _refresh.RunAsync(ct);
            var e = r.Enrichment;
            Message = $"Refresh finished: {e.AsinsResolved} product(s) matched to Amazon, {e.ProductsRefreshed} refreshed, " +
                      $"{r.Opportunities.Created} new and {r.Opportunities.Updated} updated opportunities. " +
                      (LiveKeepa ? $"{e.TokensUsed} Keepa token(s) used" + (e.TokensLeft is { } left ? $", {left} left." : ".") : "Demo data (no tokens used).") +
                      (e.ProductsDeferred > 0 ? $" {e.ProductsDeferred} item(s) wait for the next refresh (token budget)." : "") +
                      (e.Throttled ? " Keepa asked us to slow down; try again in a few minutes." : "") +
                      (e.CodesAmbiguous > 0 ? $" {e.CodesAmbiguous} product(s) map to multiple ASINs: see Unresolved." : "");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already running")) { Message = "A refresh is already running."; }
        catch (KeepaException ex) { Message = "Keepa error: " + ex.Message; }
        return Back(returnUrl);
    }

    private IActionResult Back(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToPage();

    // ---------- URL helpers: every link keeps the current filters ----------

    /// <summary>Current query string with some keys changed (null removes a key).</summary>
    public string With(params (string Key, string? Value)[] changes)
    {
        var pairs = Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        foreach (var (key, value) in changes)
        {
            if (value is null) pairs.Remove(key); else pairs[key] = value;
        }
        return pairs.Count == 0 ? "" : "?" + string.Join("&", pairs.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public string SortLink(BuyListSort key, bool firstClickDescending)
    {
        var current = EffectiveSort();
        var desc = current.Sort == key ? !current.Desc : firstClickDescending;
        return With(("Sort", key.ToString()), ("Desc", desc ? "true" : "false"), ("Page", null));
    }

    public (BuyListSort Sort, bool Desc) EffectiveSort() => Q.Sort == BuyListSort.Default
        ? (Q.Ranking == Application.Ranking.RankingMode.Raw ? BuyListSort.Monthly : BuyListSort.Adjusted, true)
        : (Q.Sort, Q.Desc);

    /// <summary>Same filters and sort, all pages.</summary>
    public string ExportHref => "/" + With(("handler", "Export"), ("Page", null));

    public string ReturnUrl => Request.Path + Request.QueryString;
}
