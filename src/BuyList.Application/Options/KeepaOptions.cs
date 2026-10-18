namespace BuyList.Application.Options;

public class KeepaOptions
{
    public const string SectionName = "Keepa";

    /// <summary>Never set in appsettings.json. User Secrets (dev) or the Keepa__ApiKey environment variable (prod).</summary>
    public string? ApiKey { get; set; }

    /// <summary>When true (default) the seeded FakeKeepaClient is used and no key is required.</summary>
    public bool UseFake { get; set; } = true;

    public string BaseUrl { get; set; } = "https://api.keepa.com/";

    /// <summary>Keepa domain id: 2 = amazon.co.uk.</summary>
    public int DomainId { get; set; } = 2;

    /// <summary>Cached snapshots younger than this are not re-fetched.</summary>
    public int RefreshAgeHours { get; set; } = 24;

    /// <summary>Stop requesting once remaining tokens fall below this.</summary>
    public int MinTokensReserve { get; set; } = 20;

    public int BatchSize { get; set; } = 20;
}
