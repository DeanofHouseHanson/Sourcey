namespace BuyList.Application.Options;

public class RankingOptions
{
    public const string SectionName = "Ranking";

    /// <summary>Budget used to cap suggested units when ranking the Buy List.</summary>
    public decimal BudgetPounds { get; set; } = 500m;

    /// <summary>Source prices older than this are "stale".</summary>
    public int StaleAfterHours { get; set; } = 24;
}
