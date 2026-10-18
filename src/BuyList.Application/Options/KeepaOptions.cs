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

    /// <summary>
    /// Planning cost per product: 1 base token + 2 for the buy box data we need. Real usage reported by Keepa
    /// (tokensConsumed) is tracked too and the larger figure wins, so the estimate can only get more cautious.
    /// </summary>
    public int TokensPerProduct { get; set; } = 3;

    /// <summary>Hard ceiling on tokens spent by one refresh run, whatever the balance is.</summary>
    public int MaxTokensPerRun { get; set; } = 100;

    /// <summary>An EAN that Keepa couldn't resolve is retried only after this many days.</summary>
    public int UnresolvedRetryDays { get; set; } = 7;

    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Base delay before retrying a transient (5xx / network) failure; doubles each retry, max 2 retries.</summary>
    public int RetryBaseDelayMs { get; set; } = 500;
}
