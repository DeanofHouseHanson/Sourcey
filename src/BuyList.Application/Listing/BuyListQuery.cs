using BuyList.Application.Ranking;
using BuyList.Domain.Enums;

namespace BuyList.Application.Listing;

public enum BuyListView { List = 0, Bought = 1 }

/// <summary>Sortable columns. Anything not in this list can't be used to order the query (no user text reaches SQL).</summary>
public enum BuyListSort
{
    Default = 0, Title, Buy, Retailer, Checked, Avg, Profit, Roi, Sales, Monthly, Adjusted, Risk, Days, Fba, MaxBuy, Qty, Status
}

/// <summary>
/// The Buy List filters. Defaults are the "sensible" view: profitable, Amazon not on the listing, price checked in the
/// last day, New or Checked items only, ranked by risk-adjusted monthly profit.
/// </summary>
public class BuyListQuery
{
    public const int MaxPageSize = 200;

    public BuyListView View { get; set; } = BuyListView.List;

    public decimal? MinRoi { get; set; }

    /// <summary>Defaults to 0 so loss-making rows are hidden; clear the box to see them.</summary>
    public decimal? MinNetProfit { get; set; } = 0m;

    public int? MinMonthlySales { get; set; }

    /// <summary>Hides anything riskier than this.</summary>
    public RiskLevel? MaxRisk { get; set; }

    public bool HideAmazon { get; set; } = true;

    /// <summary>Hides Gated, Hazmat and IP-complaint-prone items (manual toggles now, SP-API later).</summary>
    public bool HideFlagged { get; set; }

    public decimal? MaxUnitCost { get; set; }
    public string? Retailer { get; set; }

    /// <summary>Include rows whose source price is older than Ranking:StaleAfterHours (shown greyed out).</summary>
    public bool IncludeStale { get; set; }

    /// <summary>Null = New and Checked. Ignored rows only appear if you ask for them.</summary>
    public OpportunityStatus? Status { get; set; }

    public RankingMode Ranking { get; set; } = RankingMode.RiskAdjusted;
    public BuyListSort Sort { get; set; } = BuyListSort.Default;
    public bool Desc { get; set; } = true;

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
}
