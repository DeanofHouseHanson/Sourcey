namespace BuyList.Application.Options;

public class RankingOptions
{
    public const string SectionName = "Ranking";

    /// <summary>Budget used to cap suggested units when ranking the Buy List.</summary>
    public decimal BudgetPounds { get; set; } = 500m;

    /// <summary>Source prices older than this are "stale".</summary>
    public int StaleAfterHours { get; set; } = 24;

    /// <summary>Suggested quantity aims to cover this many days of expected sales.</summary>
    public int TargetDaysOfCover { get; set; } = 30;

    /// <summary>Hard ceiling on the suggested quantity of any one item.</summary>
    public int MaxSuggestedQty { get; set; } = 50;
}
