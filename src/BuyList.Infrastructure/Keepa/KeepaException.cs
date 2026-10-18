namespace BuyList.Infrastructure.Keepa;

/// <summary>
/// Failure talking to Keepa. Messages deliberately never contain the request URL, because the API key is in its query string.
/// </summary>
public class KeepaException : Exception
{
    public int? StatusCode { get; }
    public KeepaException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
}
