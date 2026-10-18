using System.Net;
using BuyList.Application.Options;
using BuyList.Infrastructure.Keepa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static BuyList.Tests.Keepa.KeepaFixtures;

namespace BuyList.Tests.Keepa;

public class KeepaClientTests
{
    private const string Key = "SECRETKEY123";

    private sealed class ListLogger : ILogger<KeepaClient>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + exception);
    }

    private static (KeepaClient Client, ScriptedHandler Handler, ListLogger Log, KeepaTokenState State) Make(
        Action<KeepaOptions>? configure = null, string? key = Key)
    {
        var o = new KeepaOptions { ApiKey = key, UseFake = false, MinTokensReserve = 20, TokensPerProduct = 3, BatchSize = 20, RetryBaseDelayMs = 0 };
        configure?.Invoke(o);
        var handler = new ScriptedHandler();
        var state = new KeepaTokenState();
        var log = new ListLogger();
        var client = new KeepaClient(new HttpClient(handler), Options.Create(o), state, new FixedTime(), log);
        return (client, handler, log, state);
    }

    private static string[] Asins(int n) => Enumerable.Range(1, n).Select(i => $"B00TEST{i:D3}").ToArray();
    private static string Products(params string[] asins) => string.Join(",", asins.Select(a => Product(a)));

    // ---- request shape ----
    [Fact]
    public async Task Checks_the_free_token_balance_first_then_requests_with_the_expected_parameters()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500)).Json(HttpStatusCode.OK, Response(494, 6, Product("B00TEST001"), Product("B00TEST002")));

        var r = await c.GetByAsinsAsync(new[] { "B00TEST001", "B00TEST002" }, maxTokens: 100);

        Assert.StartsWith("/token?key=", h.Requests[0]);
        var q = h.Requests[1];
        Assert.StartsWith("/product?key=", q);
        Assert.Contains("&domain=2", q);                      // amazon.co.uk
        Assert.Contains("&asin=B00TEST001,B00TEST002", q);    // batched in one request
        Assert.Contains("&stats=90", q);
        Assert.Contains("&buybox=1", q);                      // needed for the Buy Box average
        Assert.Contains("&history=0", q);
        Assert.DoesNotContain("offers=", q);                  // the expensive option is never used
        Assert.Equal(2, r.Products.Count);
        Assert.Equal(6, r.TokensUsed);
        Assert.Equal(494, r.Tokens.TokensLeft);
        Assert.Empty(r.NotFetched);
    }

    [Fact]
    public async Task Code_lookups_use_the_code_parameter()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500)).Json(HttpStatusCode.OK, Response(494, 3, Product()));
        await c.GetByCodesAsync(new[] { "5012345678900" }, 100);
        Assert.Contains("&code=5012345678900", h.Requests[1]);
    }

    [Fact]
    public async Task The_api_key_is_url_encoded()
    {
        var (c, h, _, _) = Make(key: "a+b&c=d");
        h.Json(HttpStatusCode.OK, TokenOnly(500));
        await c.GetTokenStatusAsync();
        Assert.Contains("key=a%2Bb%26c%3Dd", h.Requests[0]);
    }

    // ---- never over-spend ----
    [Fact]
    public async Task Never_dips_into_the_reserve()
    {
        // 40 tokens, reserve 20, 3 per item: only (40-20)/3 = 6 of 10 may be requested.
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(40)).Json(HttpStatusCode.OK, Response(22, 18, Products(Asins(6))));

        var r = await c.GetByAsinsAsync(Asins(10), maxTokens: 1000);

        Assert.Equal(6, r.Products.Count);
        Assert.Equal(Asins(10).Skip(6), r.NotFetched);
        Assert.Contains("B00TEST006", h.Requests[1]);
        Assert.DoesNotContain("B00TEST007", h.Requests[1]);
    }

    [Fact]
    public async Task Respects_the_per_call_token_cap()
    {
        // plenty of tokens, but the caller allows only 9 => 3 items at 3 tokens each.
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(1000)).Json(HttpStatusCode.OK, Response(991, 9, Products(Asins(3))));

        var r = await c.GetByAsinsAsync(Asins(10), maxTokens: 9);

        Assert.Equal(3, r.Products.Count);
        Assert.Equal(7, r.NotFetched.Count);
        Assert.Equal(9, r.TokensUsed);
    }

    [Fact]
    public async Task Sends_nothing_when_the_balance_is_at_or_below_the_reserve()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(20));

        var r = await c.GetByAsinsAsync(Asins(5), 100);

        Assert.Empty(h.ProductRequests);
        Assert.Equal(5, r.NotFetched.Count);
        Assert.Equal(0, r.TokensUsed);
    }

    [Fact]
    public async Task Negative_balance_sends_nothing()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(-15));
        var r = await c.GetByAsinsAsync(Asins(2), 100);
        Assert.Empty(h.ProductRequests);
        Assert.Equal(2, r.NotFetched.Count);
    }

    [Fact]
    public async Task Splits_into_batches_and_replans_from_each_response()
    {
        // batch size 4. Balance 100 -> first batch of 4. That response says only 26 tokens remain,
        // so (26-20)/3 = 2 more items may go; the last 4 are left for a later run.
        var (c, h, _, _) = Make(o => o.BatchSize = 4);
        h.Json(HttpStatusCode.OK, TokenOnly(100))
         .Json(HttpStatusCode.OK, Response(26, 12, Products(Asins(4))))
         .Json(HttpStatusCode.OK, Response(20, 6, Products(Asins(10)[4..6])));

        var r = await c.GetByAsinsAsync(Asins(10), 1000);

        Assert.Equal(2, h.ProductRequests.Count());
        Assert.Equal(6, r.Products.Count);
        Assert.Equal(Asins(10).Skip(6), r.NotFetched);
        Assert.Equal(18, r.TokensUsed);
    }

    [Fact]
    public async Task Learns_a_higher_real_cost_per_item_and_plans_with_it()
    {
        // Keepa charges 5/item for the first call (estimate was 3); the second call must plan at 5.
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(100))
         .Json(HttpStatusCode.OK, Response(90, 10, Products(Asins(2))));
        await c.GetByAsinsAsync(Asins(2), 1000);

        // 90 left, reserve 20 => 70 / 5 = 14 items (not 70 / 3 = 23)
        h.Json(HttpStatusCode.OK, Response(20, 70, Products(Asins(14))));   // back at the reserve
        var r = await c.GetByAsinsAsync(Asins(30).Skip(0).Take(30).ToArray(), 1000);

        Assert.Equal(14, r.Products.Count);
        Assert.Equal(16, r.NotFetched.Count);
    }

    [Fact]
    public async Task Code_lookups_are_planned_at_double_cost_and_small_batches()
    {
        // 6 tokens/code. Balance 70, reserve 20 => 50/6 = 8, capped by the code batch size of 5.
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(70)).Json(HttpStatusCode.OK, Response(20, 30, Product()));   // leaves exactly the reserve
        var codes = Enumerable.Range(0, 7).Select(i => $"50123456789{i:D2}").ToArray();   // not all valid GTINs, but the format is
        var r = await c.GetByCodesAsync(codes, 1000);

        Assert.Single(h.ProductRequests);
        Assert.Equal(codes.Take(5), h.ProductRequests.Single().Split("&code=")[1].Split('&')[0].Split(','));
        Assert.Equal(codes.Skip(5), r.NotFetched);   // the balance is back at the reserve, so the last 2 wait
    }

    // ---- throttling and errors ----
    [Fact]
    public async Task A_429_stops_the_run_and_reports_what_was_not_fetched_without_throwing()
    {
        var (c, h, _, s) = Make(o => o.BatchSize = 2);
        h.Json(HttpStatusCode.OK, TokenOnly(500))
         .Json(HttpStatusCode.OK, Response(494, 6, Products(Asins(2))))
         .Json((HttpStatusCode)429, "{\"tokensLeft\":-4,\"refillIn\":12000,\"refillRate\":20}");

        var r = await c.GetByAsinsAsync(Asins(6), 1000);

        Assert.True(r.Throttled);
        Assert.Equal(2, r.Products.Count);
        Assert.Equal(Asins(6).Skip(2), r.NotFetched);
        Assert.Equal(-4, s.Last.TokensLeft);        // the balance in the error body is remembered
        Assert.Equal(12000, s.Last.RefillInMs);
        Assert.Equal(2, h.ProductRequests.Count()); // it did not keep hammering
    }

    [Fact]
    public async Task Transient_server_errors_are_retried_then_succeed()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500))
         .Json(HttpStatusCode.InternalServerError, "oops")
         .Json(HttpStatusCode.BadGateway, "oops")
         .Json(HttpStatusCode.OK, Response(494, 3, Product()));

        var r = await c.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100);

        Assert.Single(r.Products);
        Assert.Equal(3, h.ProductRequests.Count());
    }

    [Fact]
    public async Task Persistent_server_errors_give_up_after_bounded_retries()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500));
        for (var i = 0; i < 3; i++) h.Json(HttpStatusCode.InternalServerError, "oops");

        var ex = await Assert.ThrowsAsync<KeepaException>(() => c.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100));
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal(3, h.ProductRequests.Count());   // 1 try + 2 retries
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500)).Json(HttpStatusCode.PaymentRequired, "{}");
        var ex = await Assert.ThrowsAsync<KeepaException>(() => c.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100));
        Assert.Equal(402, ex.StatusCode);
        Assert.Single(h.ProductRequests);
    }

    [Fact]
    public async Task Network_failures_become_keepa_exceptions_without_the_url()
    {
        var (c, h, _, _) = Make();
        for (var i = 0; i < 3; i++) h.Throw(new HttpRequestException("connection refused to https://api.keepa.com/token?key=" + Key));
        var ex = await Assert.ThrowsAsync<KeepaException>(() => c.GetTokenStatusAsync());
        Assert.DoesNotContain(Key, ex.ToString());
    }

    [Fact]
    public async Task Timeouts_are_retried_then_reported()
    {
        var (c, h, _, _) = Make();
        for (var i = 0; i < 3; i++) h.Throw(new TaskCanceledException("timeout"));
        var ex = await Assert.ThrowsAsync<KeepaException>(() => c.GetTokenStatusAsync());
        Assert.Contains("did not respond", ex.Message);
        Assert.Equal(3, h.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        var (c, h, _, _) = Make();
        h.Throw(new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetTokenStatusAsync(cts.Token));
    }

    [Fact]
    public async Task Invalid_json_is_a_clear_error()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, "<html>not json</html>");
        await Assert.ThrowsAsync<KeepaException>(() => c.GetTokenStatusAsync());
    }

    // ---- safety ----
    [Fact]
    public async Task Missing_key_fails_before_any_http_call()
    {
        var (c, h, _, _) = Make(key: null);
        var ex = await Assert.ThrowsAsync<KeepaException>(() => c.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100));
        Assert.Contains("API key", ex.Message);
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData("B00TEST&key=x")]
    [InlineData("short")]
    [InlineData("b00testaaa")]
    [InlineData("B00TESTAAA,B00TESTBBB")]
    public async Task Malformed_asins_are_rejected_so_nothing_can_be_injected_into_the_query(string asin)
    {
        var (c, h, _, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetByAsinsAsync(new[] { asin }, 100));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Malformed_codes_are_rejected()
    {
        var (c, h, _, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetByCodesAsync(new[] { "123&x=1" }, 100));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task The_api_key_never_appears_in_logs_or_exceptions()
    {
        var (c, h, log, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500))
         .Json(HttpStatusCode.InternalServerError, "oops").Json(HttpStatusCode.InternalServerError, "oops").Json(HttpStatusCode.InternalServerError, "oops");
        var ex = await Assert.ThrowsAsync<KeepaException>(() => c.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100));

        // also a successful run for the info-level log line
        var (c2, h2, log2, _) = Make();
        h2.Json(HttpStatusCode.OK, TokenOnly(500)).Json(HttpStatusCode.OK, Response(494, 3, Product()));
        await c2.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100);

        Assert.DoesNotContain(Key, ex.ToString());
        Assert.All(log.Lines.Concat(log2.Lines), l => Assert.DoesNotContain(Key, l));
        Assert.NotEmpty(log2.Lines);
    }

    [Fact]
    public async Task Duplicate_items_are_requested_once()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500)).Json(HttpStatusCode.OK, Response(494, 3, Product()));
        await c.GetByAsinsAsync(new[] { "B00TESTAAA", "B00TESTAAA" }, 100);
        Assert.Contains("&asin=B00TESTAAA&", h.Requests[1] + "&");
    }

    [Fact]
    public async Task Token_status_is_not_re_fetched_when_recent()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(500))
         .Json(HttpStatusCode.OK, Response(494, 3, Product()))
         .Json(HttpStatusCode.OK, Response(488, 3, Product()));
        await c.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100);
        await c.GetByAsinsAsync(new[] { "B00TESTAAA" }, 100);
        Assert.Equal(1, h.TokenRequests);
    }

    [Fact]
    public async Task Token_status_call_is_parsed()
    {
        var (c, h, _, _) = Make();
        h.Json(HttpStatusCode.OK, TokenOnly(321));
        var s = await c.GetTokenStatusAsync();
        Assert.Equal(321, s.TokensLeft);
        Assert.Equal(20, s.RefillRatePerMinute);
    }
}
