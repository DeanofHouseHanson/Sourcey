using System.Net;
using System.Text;
using System.Text.Json;

namespace BuyList.Tests.Keepa;

/// <summary>
/// Hand-written responses modelled on Keepa's documented product format (NOT recorded from the live API):
/// prices are integer pence, -1 means "no data", and stats arrays are indexed by csv type
/// (0 Amazon, 3 sales rank, 11 new-offer count, 18 Buy Box incl. shipping, 34 new FBA offer count).
/// </summary>
public static class KeepaFixtures
{
    public static string Arr(params (int Index, int Value)[] values)
    {
        var a = Enumerable.Repeat(-1, 35).ToArray();
        foreach (var (i, v) in values) a[i] = v;
        return "[" + string.Join(",", a) + "]";
    }

    /// <summary>A product with sensible defaults; override pieces as raw JSON fragments.</summary>
    public static string Product(string asin = "B00TESTAAA", string? current = null, string? avg90 = null, string extra = "",
        string eans = "[\"5012345678900\"]", bool stats = true)
    {
        var statsJson = stats
            ? $"\"stats\":{{\"current\":{current ?? Arr((0, -1), (3, 1234), (11, 9), (34, 4))},\"avg90\":{avg90 ?? Arr((3, 1500), (18, 2199))},\"salesRankDrops30\":55}},"
            : "";
        return $"{{\"asin\":\"{asin}\",\"title\":\"Test Widget 750ml\",\"brand\":\"Norvik\",\"partNumber\":\"NV-IB750\"," +
               $"\"eanList\":{eans},\"upcList\":[\"036000291452\"],\"imagesCSV\":\"71abc.jpg,81def.jpg\",\"packageWeight\":420," +
               $"\"monthlySold\":150,\"fbaFees\":{{\"pickAndPackFee\":289,\"pickAndPackFeeTax\":58}},{statsJson}\"lastUpdate\":1}}{(extra.Length > 0 ? "," + extra : "")}"
                   .Replace("\"lastUpdate\":1}", "\"lastUpdate\":1}");
    }

    public static string Response(int tokensLeft, int consumed, params string[] products) =>
        $"{{\"timestamp\":1,\"tokensLeft\":{tokensLeft},\"refillIn\":30000,\"refillRate\":20,\"tokenFlowReduction\":0.0," +
        $"\"tokensConsumed\":{consumed},\"processingTimeInMs\":5,\"products\":[{string.Join(",", products)}]}}";

    public static string TokenOnly(int tokensLeft) =>
        $"{{\"timestamp\":1,\"tokensLeft\":{tokensLeft},\"refillIn\":30000,\"refillRate\":20,\"tokenFlowReduction\":0.0,\"processingTimeInMs\":1}}";

    public static JsonDocument Doc(string json) => JsonDocument.Parse(json);
}

/// <summary>Returns scripted responses in order and records each request (path + query).</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _script = new();
    public List<string> Requests { get; } = new();

    public ScriptedHandler Json(HttpStatusCode status, string body) =>
        Then(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public ScriptedHandler Then(Func<HttpResponseMessage> response) { _script.Enqueue(response); return this; }
    public ScriptedHandler Throw(Exception ex) => Then(() => throw ex);

    public IEnumerable<string> ProductRequests => Requests.Where(r => r.StartsWith("/product"));
    public int TokenRequests => Requests.Count(r => r.StartsWith("/token"));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!.PathAndQuery);
        if (_script.Count == 0) throw new InvalidOperationException("Unscripted request: " + request.RequestUri!.AbsolutePath);
        return Task.FromResult(_script.Dequeue()());
    }
}
