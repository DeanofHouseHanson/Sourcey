using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BuyList.Tests.Web;

public class KeepaStartupTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _config;
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"buylist-startup-{Guid.NewGuid():N}.db");
        public Factory(Dictionary<string, string?> config) => _config = config;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            _config["ConnectionStrings:BuyList"] = $"Data Source={_db}";
            _config["Seed:OnStartup"] = "false";
            foreach (var (k, v) in _config) builder.UseSetting(k, v);
        }
    }

    [Fact]
    public void Live_keepa_without_a_key_refuses_to_start_with_a_clear_message()
    {
        using var f = new Factory(new() { ["Keepa:UseFake"] = "false", ["Keepa:ApiKey"] = "" });
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("no API key", ex.ToString());
    }

    [Fact]
    public async Task Live_keepa_with_a_key_starts_without_contacting_keepa()
    {
        // An unreachable base URL proves nothing is called at startup: any request would fail the host.
        using var f = new Factory(new() { ["Keepa:UseFake"] = "false", ["Keepa:ApiKey"] = "test-key-not-real", ["Keepa:BaseUrl"] = "https://keepa.invalid/" });
        var res = await f.CreateClient().GetAsync("/Account/Login");
        Assert.True(res.IsSuccessStatusCode);
    }

    [Fact]
    public void The_fake_client_needs_no_key()
    {
        using var f = new Factory(new() { ["Keepa:UseFake"] = "true" });
        f.CreateClient();
    }
}
