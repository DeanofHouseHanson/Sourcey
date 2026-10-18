using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuyList.Application.Keepa;
using BuyList.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuyList.Infrastructure.Keepa;

/// <summary>
/// Real Keepa client. Token safety, in order of importance:
///  * the balance is read from the free /token endpoint before spending, never after;
///  * a call spends at most <c>maxTokens</c> and never touches <c>MinTokensReserve</c>;
///  * the per-item cost used for planning is the larger of the configured estimate and what Keepa has actually charged;
///  * code (EAN) lookups can return several products per code, so they are planned at double cost and sent in small batches;
///  * a 429 stops everything and reports what wasn't fetched.
/// The API key travels in the query string (Keepa has no header option), so URLs are never logged or put in exceptions.
/// </summary>
public class KeepaClient : IKeepaClient
{
    private static readonly Regex AsinPattern = new("^[A-Z0-9]{10}$", RegexOptions.Compiled);
    private static readonly Regex CodePattern = new("^[0-9]{8,14}$", RegexOptions.Compiled);
    private const int MaxCodeBatch = 5;
    private const int MaxRetries = 2;
    private static readonly TimeSpan StatusMaxAge = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly KeepaOptions _o;
    private readonly KeepaTokenState _state;
    private readonly TimeProvider _time;
    private readonly ILogger<KeepaClient> _log;

    public KeepaClient(HttpClient http, IOptions<KeepaOptions> options, KeepaTokenState state, TimeProvider time, ILogger<KeepaClient> log)
    {
        _http = http;
        _o = options.Value;
        _state = state;
        _time = time;
        _log = log;
        _http.BaseAddress ??= new Uri(_o.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _o.RequestTimeoutSeconds));
    }

    public async Task<KeepaTokenStatus> GetTokenStatusAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync("token", "", ct);
        var status = KeepaResponseParser.ParseStatus(doc.RootElement);
        _state.Observe(status, _time.GetUtcNow());
        return _state.Last;
    }

    public Task<KeepaFetchResult> GetByAsinsAsync(IReadOnlyList<string> asins, int maxTokens, CancellationToken ct = default)
    {
        var bad = asins.FirstOrDefault(a => !AsinPattern.IsMatch(a));
        if (bad is not null) throw new ArgumentException($"'{bad}' is not a valid ASIN.", nameof(asins));
        return FetchAsync("asin", asins, maxTokens, Math.Min(_o.BatchSize, 100), costMultiplier: 1, ct);
    }

    public Task<KeepaFetchResult> GetByCodesAsync(IReadOnlyList<string> codes, int maxTokens, CancellationToken ct = default)
    {
        var bad = codes.FirstOrDefault(c => !CodePattern.IsMatch(c));
        if (bad is not null) throw new ArgumentException($"'{bad}' is not a valid EAN/UPC.", nameof(codes));
        return FetchAsync("code", codes, maxTokens, Math.Min(_o.BatchSize, MaxCodeBatch), costMultiplier: 2, ct);
    }

    private async Task<KeepaFetchResult> FetchAsync(string kind, IReadOnlyList<string> requested, int maxTokens, int batchSize, int costMultiplier, CancellationToken ct)
    {
        var items = requested.Distinct().ToList();
        var products = new List<KeepaProduct>();
        var sent = 0;
        var spent = 0;
        var throttled = false;

        // Free call; makes sure we plan from a fresh balance rather than one remembered from long ago.
        var observed = _state.ObservedAt;
        if (items.Count > 0 && (observed is null || _time.GetUtcNow() - observed.Value > StatusMaxAge))
            await GetTokenStatusAsync(ct);

        while (sent < items.Count && !throttled)
        {
            // Re-plan before every batch from the freshest balance and cost figures.
            var perItem = Math.Max(_o.TokensPerProduct, _state.ObservedCostPerItem) * costMultiplier;
            var tokensLeft = _state.Last.TokensLeft ?? 0;
            var affordable = Math.Min(maxTokens - spent, tokensLeft - _o.MinTokensReserve);
            var count = Math.Min(Math.Min(batchSize, items.Count - sent), Math.Max(0, affordable) / perItem);
            if (count < 1) break;

            var batch = items.GetRange(sent, count);
            var query = $"&domain={_o.DomainId}&{kind}={string.Join(",", batch)}&stats=90&history=0&buybox=1";
            try
            {
                using var doc = await SendAsync("product", query, ct);
                var status = KeepaResponseParser.ParseStatus(doc.RootElement);
                _state.Observe(status, _time.GetUtcNow());
                var consumed = KeepaResponseParser.ParseTokensConsumed(doc.RootElement);
                _state.ObserveCost(consumed, batch.Count);
                spent += consumed;
                sent += count;
                products.AddRange(KeepaResponseParser.ParseProducts(doc.RootElement, _time.GetUtcNow().UtcDateTime));
                _log.LogInformation("Keepa: fetched {Items} item(s), {Consumed} token(s) used, {Left} left", count, consumed, status.TokensLeft);
            }
            catch (KeepaException ex) when (ex.StatusCode == 429)
            {
                throttled = true;   // balance was lower than we thought; leave this batch unsent
                _log.LogWarning("Keepa reports not enough tokens; stopping this run.");
            }
        }

        return new KeepaFetchResult(products, items.Skip(sent).ToList(), spent, _state.Last, throttled);
    }

    /// <summary>One GET with bounded retries for transient failures. Callers get a parsed document or a KeepaException.</summary>
    private async Task<JsonDocument> SendAsync(string path, string extraQuery, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.ApiKey))
            throw new KeepaException("No Keepa API key configured (set Keepa:ApiKey via user secrets or the Keepa__ApiKey environment variable).");

        var uri = $"{path}?key={Uri.EscapeDataString(_o.ApiKey)}{extraQuery}";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var res = await _http.GetAsync(uri, ct);
                var body = await res.Content.ReadAsStringAsync(ct);

                if (res.StatusCode == HttpStatusCode.OK)
                {
                    try { return JsonDocument.Parse(body); }
                    catch (JsonException) { throw new KeepaException("Keepa returned a response that isn't valid JSON.", 200); }
                }

                // Error bodies still carry the token balance; record it so we don't over-spend on the next call.
                TryObserve(body);
                var code = (int)res.StatusCode;
                if (code >= 500 && attempt < MaxRetries) { await BackoffAsync(attempt, ct); continue; }
                throw new KeepaException(Describe(code), code);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _log.LogWarning("Keepa request failed ({Kind}); retrying.", ex.StatusCode?.ToString() ?? ex.GetType().Name);
                await BackoffAsync(attempt, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                await BackoffAsync(attempt, ct);   // HttpClient timeout, not caller cancellation
            }
            catch (HttpRequestException ex)
            {
                throw new KeepaException($"Could not reach Keepa ({ex.StatusCode?.ToString() ?? ex.GetType().Name}).");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new KeepaException("Keepa did not respond in time.");
            }
        }
    }

    private void TryObserve(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            _state.Observe(KeepaResponseParser.ParseStatus(doc.RootElement), _time.GetUtcNow());
        }
        catch (JsonException) { /* non-JSON error page: nothing to learn */ }
    }

    private Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = _o.RetryBaseDelayMs * (1 << attempt);
        return ms <= 0 ? Task.CompletedTask : Task.Delay(ms + Random.Shared.Next(0, Math.Max(1, ms / 4)), ct);
    }

    private static string Describe(int code) => code switch
    {
        400 => "Keepa rejected the request (400 REQUEST_REJECTED).",
        402 => "Keepa says payment is required (402): check the API key and subscription.",
        404 => "Keepa returned 404 NOT_FOUND.",
        429 => "Keepa has not enough tokens (429).",
        >= 500 => $"Keepa server error ({code}).",
        _ => $"Keepa request failed with status {code}."
    };
}
