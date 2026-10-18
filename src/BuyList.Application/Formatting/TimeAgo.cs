namespace BuyList.Application.Formatting;

public static class TimeAgo
{
    /// <summary>"just now", "5m ago", "3h ago", "2d ago". Future times (clock skew) read as "just now".</summary>
    public static string Format(DateTime thenUtc, DateTime nowUtc)
    {
        var span = nowUtc - thenUtc;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}
