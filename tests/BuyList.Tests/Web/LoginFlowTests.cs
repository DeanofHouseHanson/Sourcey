using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace BuyList.Tests.Web;

public sealed class LoginFlowTests : IClassFixture<LoginFlowTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"buylist-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); // uses the dev fallback password "changeme"
            // UseSetting (not ConfigureAppConfiguration): Program.cs reads configuration before the host is built.
            builder.UseSetting("ConnectionStrings:BuyList", $"Data Source={_db}");
            builder.UseSetting("Seed:OnStartup", "false");
        }
    }

    private readonly Factory _factory;
    public LoginFlowTests(Factory f) => _factory = f;

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task Anonymous_users_are_redirected_to_login()
    {
        var res = await Client().GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/Account/Login", res.Headers.Location!.IsAbsoluteUri ? res.Headers.Location.PathAndQuery : res.Headers.Location.OriginalString);
    }

    [Fact]
    public async Task Login_page_is_public()
    {
        var res = await Client().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Valid_login_grants_access_and_bad_login_does_not()
    {
        var c = Client();
        var token = await Token(c);

        var bad = await c.PostAsync("/Account/Login", Form(token, "admin", "wrong"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode); // re-rendered with error

        token = await Token(c);
        var ok = await c.PostAsync("/Account/Login", Form(token, "admin", "changeme"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        var home = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Buy List", await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Open_redirect_via_return_url_is_ignored()
    {
        var c = Client();
        var token = await Token(c);
        var res = await c.PostAsync("/Account/Login", Form(token, "admin", "changeme", "https://evil.example/"));
        Assert.Equal("/", res.Headers.Location!.OriginalString);
    }

    private static FormUrlEncodedContent Form(string token, string user, string pw, string? returnUrl = null) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Username"] = user, ["Password"] = pw, ["ReturnUrl"] = returnUrl ?? ""
        });

    private static async Task<string> Token(HttpClient c)
    {
        var html = await (await c.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        return Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    }
}
