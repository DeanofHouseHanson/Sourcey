namespace BuyList.Application.Keepa;

/// <summary>
/// Source of Amazon market data. Implementations must never spend more than <c>maxTokens</c> in one call, and must
/// leave the configured reserve untouched; anything they decline to fetch is returned in <c>NotFetched</c>.
/// </summary>
public interface IKeepaClient
{
    /// <summary>Current token balance. Free on the real API (costs no tokens).</summary>
    Task<KeepaTokenStatus> GetTokenStatusAsync(CancellationToken ct = default);

    Task<KeepaFetchResult> GetByAsinsAsync(IReadOnlyList<string> asins, int maxTokens, CancellationToken ct = default);

    /// <summary>Look products up by EAN/UPC/ISBN-13 (canonical GTINs).</summary>
    Task<KeepaFetchResult> GetByCodesAsync(IReadOnlyList<string> codes, int maxTokens, CancellationToken ct = default);
}
