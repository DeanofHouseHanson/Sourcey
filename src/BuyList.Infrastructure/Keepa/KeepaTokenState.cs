using BuyList.Application.Keepa;

namespace BuyList.Infrastructure.Keepa;

/// <summary>Process-wide record of the token balance and the real per-item cost, shared by every KeepaClient call.</summary>
public sealed class KeepaTokenState
{
    private readonly object _gate = new();
    private KeepaTokenStatus _last = new(null, null, null);
    private DateTimeOffset? _observedAt;
    private int _observedCostPerItem;

    public KeepaTokenStatus Last { get { lock (_gate) return _last; } }
    public DateTimeOffset? ObservedAt { get { lock (_gate) return _observedAt; } }

    /// <summary>Highest per-item cost Keepa has actually charged this session; the budget can only become more cautious.</summary>
    public int ObservedCostPerItem { get { lock (_gate) return _observedCostPerItem; } }

    public void Observe(KeepaTokenStatus status, DateTimeOffset now)
    {
        lock (_gate)
        {
            // Keep earlier values for anything this response didn't carry.
            _last = new KeepaTokenStatus(
                status.TokensLeft ?? _last.TokensLeft,
                status.RefillRatePerMinute ?? _last.RefillRatePerMinute,
                status.RefillInMs ?? _last.RefillInMs);
            if (status.TokensLeft is not null) _observedAt = now;
        }
    }

    public void ObserveCost(int tokensConsumed, int itemsRequested)
    {
        if (tokensConsumed <= 0 || itemsRequested <= 0) return;
        var perItem = (int)Math.Ceiling(tokensConsumed / (double)itemsRequested);
        lock (_gate) _observedCostPerItem = Math.Max(_observedCostPerItem, perItem);
    }
}
